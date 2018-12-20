﻿using FreeSql.DataAnnotations;
using FreeSql.Internal.Model;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace FreeSql.Internal {
	class Utils {

		static ConcurrentDictionary<string, TableInfo> _cacheGetTableByEntity = new ConcurrentDictionary<string, TableInfo>();
		internal static TableInfo GetTableByEntity(Type entity, CommonUtils common) {
			if (_cacheGetTableByEntity.TryGetValue(entity.FullName, out var trytb)) return trytb;
			if (common.CodeFirst.GetDbInfo(entity) != null) return null;

			var tbattr = entity.GetCustomAttributes(typeof(TableAttribute), false).LastOrDefault() as TableAttribute;
			trytb = new TableInfo();
			trytb.Type = entity;
			trytb.Properties = entity.GetProperties().ToDictionary(a => a.Name, a => a, StringComparer.CurrentCultureIgnoreCase);
			trytb.CsName = entity.Name;
			trytb.DbName = tbattr?.Name ?? entity.Name;
			trytb.DbOldName = tbattr?.OldName;
			trytb.SelectFilter = tbattr?.SelectFilter;
			foreach (var p in trytb.Properties.Values) {
				var tp = common.CodeFirst.GetDbInfo(p.PropertyType);
				//if (tp == null) continue;
				var colattr = p.GetCustomAttributes(typeof(ColumnAttribute), false).LastOrDefault() as ColumnAttribute;
				if (tp == null && colattr == null) continue;
				if (colattr == null)
					colattr = new ColumnAttribute {
						Name = p.Name,
						DbType = tp.Value.dbtypeFull,
						IsIdentity = false,
						IsNullable = tp.Value.isnullable ?? false,
						IsPrimary = false,
					};
				if (string.IsNullOrEmpty(colattr.Name)) colattr.Name = p.Name;
				if (string.IsNullOrEmpty(colattr.DbType)) colattr.DbType = tp?.dbtypeFull ?? "varchar(255)";
				if (colattr.DbType.IndexOf("NOT NULL") == -1 && tp?.isnullable == false) colattr.DbType += " NOT NULL";

				var col = new ColumnInfo {
					Table = trytb,
					CsName = p.Name,
					CsType = p.PropertyType,
					Attribute = colattr
				};
				trytb.Columns.Add(colattr.Name, col);
				trytb.ColumnsByCs.Add(p.Name, col);
			}
			trytb.Primarys = trytb.Columns.Values.Where(a => a.Attribute.IsPrimary).ToArray();
			_cacheGetTableByEntity.TryAdd(entity.FullName, trytb);
			return trytb;
		}

		internal static T[] GetDbParamtersByObject<T>(string sql, object obj, string paramPrefix, Func<string, Type, object, T> constructorParamter) {
			if (string.IsNullOrEmpty(sql) || obj == null) return new T[0];
			var ttype = typeof(T);
			var type = obj.GetType();
			if (type == ttype) return new[] { (T)Convert.ChangeType(obj, type) };
			var ret = new List<T>();
			var ps = type.GetProperties();
			foreach (var p in ps) {
				if (sql.IndexOf($"{paramPrefix}{p.Name}", StringComparison.CurrentCultureIgnoreCase) == -1) continue;
				var pvalue = p.GetValue(obj);
				if (p.PropertyType == ttype) ret.Add((T)Convert.ChangeType(pvalue, ttype));
				else ret.Add(constructorParamter(p.Name, p.PropertyType, pvalue));
			}
			return ret.ToArray();
		}

		internal static (object value, int dataIndex) ExecuteArrayRowReadClassOrTuple(Type type, Dictionary<string, int> names, object[] row, int dataIndex = 0) {
			if (type.Namespace == "System" && (type.FullName == "System.String" || type.IsValueType)) { //值类型，或者元组
				bool isTuple = type.Name.StartsWith("ValueTuple`");
				if (isTuple) {
					var fs = type.GetFields();
					var types = new Type[fs.Length];
					var parms = new object[fs.Length];
					for (int a = 0; a < fs.Length; a++) {
						types[a] = fs[a].FieldType;
						var read = ExecuteArrayRowReadClassOrTuple(types[a], names, row, dataIndex);
						if (read.dataIndex > dataIndex) dataIndex = read.dataIndex;
						parms[a] = read.value;
					}
					var constructor = type.GetConstructor(types);
					return (constructor?.Invoke(parms), dataIndex);
				}
				return (dataIndex >= row.Length || row[dataIndex] == DBNull.Value ? null : Convert.ChangeType(row[dataIndex], type), dataIndex + 1);
			}
			if (type == typeof(object) && names != null) {
				dynamic expando = new System.Dynamic.ExpandoObject(); //动态类型字段 可读可写
				var expandodic = (IDictionary<string, object>)expando;
				foreach (var name in names)
					expandodic[Utils.GetCsName(name.Key)] = row[name.Value];
				return (expando, names.Count);
			}
			//类注入属性
			var consturct = type.GetConstructor(new Type[0]);
			var value = consturct.Invoke(new object[0]);
			var ps = type.GetProperties();
			foreach(var p in ps) {
				var tryidx = dataIndex;
				if (names != null && names.TryGetValue(p.Name, out tryidx) == false) continue;
				var read = ExecuteArrayRowReadClassOrTuple(p.PropertyType, names, row, tryidx);
				if (read.dataIndex > dataIndex) dataIndex = read.dataIndex;
				FillPropertyValue(value, p.Name, read.value);
				//p.SetValue(value, read.value);
			}
			return (value, dataIndex);
		}

		internal static void FillPropertyValue(object info, string memberAccessPath, object value) {
			var current = info;
			PropertyInfo prop = null;
			var members = memberAccessPath.Split('.');
			for (var a = 0; a < members.Length; a++) {
				var type = current.GetType();
				prop = type.GetProperty(members[a], BindingFlags.Public | BindingFlags.IgnoreCase | BindingFlags.Instance);
				if (prop == null) throw new Exception(string.Concat(type.FullName, " 没有定义属性 ", members[a]));
				if (a < members.Length - 1) current = prop.GetValue(current);
			}
			if (value == null || value == DBNull.Value) {
				prop.SetValue(current, null, null);
				return;
			}
			var propType = prop.PropertyType;
			if (propType.FullName.StartsWith("System.Nullable`1[")) propType = propType.GenericTypeArguments.First();
			if (propType.IsEnum) {
				var valueStr = string.Concat(value);
				if (string.IsNullOrEmpty(valueStr) == false) prop.SetValue(current, Enum.Parse(propType, valueStr), null);
				return;
			}
			if (propType != value.GetType()) {
				prop.SetValue(current, Convert.ChangeType(value, propType), null);
				return;
			}
			prop.SetValue(current, value, null);
		}
		internal static string GetCsName(string name) {
			name = Regex.Replace(name.TrimStart('@'), @"[^\w]", "_");
			return char.IsLetter(name, 0) ? name : string.Concat("_", name);
		}
	}
}