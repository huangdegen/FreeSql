﻿using System;
using System.Collections.Generic;
using System.Text;


public class g {

	public static IFreeSql mysql = new FreeSql.FreeSqlBuilder()
		.UseConnectionString(FreeSql.DataType.MySql, "Data Source=127.0.0.1;Port=3306;User ID=root;Password=root;Initial Catalog=cccddd;Charset=utf8;SslMode=none;Max pool size=10")
		.Build();

	public static IFreeSql sqlserver = new FreeSql.FreeSqlBuilder()
		.UseConnectionString(FreeSql.DataType.SqlServer, "Data Source=.;Integrated Security=True;Initial Catalog=cms;Pooling=true;Max Pool Size=10")
		.Build();
}