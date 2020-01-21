﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.IO;

using adb.stat;
using adb.sqlparser;
using adb.expr;
using adb.logic;
using adb.physic;
using adb.index;
using adb.test;

using TableColumn = System.Tuple<string, string>;

namespace adb
{
    public class ColumnDef
    {
        readonly public string name_;
        readonly public ColumnType type_;
        public int ordinal_;

        public ColumnDef(string name, ColumnType type, int ord) { name_ = name; type_ = type; ordinal_ = ord; }
        public ColumnDef(string name, int ord) : this(name, new IntType(), ord) { }

        public override string ToString() => $"{name_} {type_} [{ordinal_}]";
    }

    public class TableDef
    {
        public string name_;
        public Dictionary<string, ColumnDef> columns_;
        public List<IndexDef> indexes_ = new List<IndexDef>();

        // storage
        public List<Row> heap_ = new List<Row>();

        public TableDef(string tabName, List<ColumnDef> columns)
        {
            Dictionary<string, ColumnDef> cols = new Dictionary<string, ColumnDef>();
            foreach (var c in columns)
                cols.Add(c.name_, c);
            name_ = tabName; columns_ = cols;
        }

        public List<ColumnDef> ColumnsInOrder() {
            var list = columns_.Values.ToList();
            return list.OrderBy(x => x.ordinal_).ToList();
        }

        public ColumnDef GetColumn(string column)
        {
            columns_.TryGetValue(column, out var value);
            return value;
        }
    }

    public class SystemTable
    {
    };

    // format: tableName:key, list of <ColName: Key, Column definition>
    public class SysTable : SystemTable
    {
        readonly Dictionary<string, TableDef> records_ = new Dictionary<string, TableDef>();

        public void Create(string tabName, List<ColumnDef> columns)
        {
            records_.Add(tabName,
                new TableDef(tabName, columns));
        }
        public void Drop(string tabName)
        {
            records_.Remove(tabName);
            // FIXME: we shall also remove index etc
        }

        public TableDef TryTable(string tabName) {
            if (records_.TryGetValue(tabName, out TableDef value))
                return value;
            return null;
        }
        public TableDef Table(string tabName)=> records_[tabName];
        public Dictionary<string, ColumnDef> TableCols(string tabName)=> records_[tabName].columns_;
        public ColumnDef Column(string tabName, string colName)=> TableCols(tabName)[colName];
    }

    public static class Catalog
    {
        // list of system tables
        public static SysTable systable_ = new SysTable();
        public static SysStats sysstat_ = new SysStats();

        static void createBuildInTestTables()
        {
            // create tables
            string[] createtables = {
                @"create table test (a1 int, a2 int, a3 int, a4 int);"
                ,
                @"create table a (a1 int, a2 int, a3 int, a4 int);",
                @"create table b (b1 int, b2 int, b3 int, b4 int);",
                @"create table c (c1 int, c2 int, c3 int, c4 int);",
                @"create table d (d1 int, d2 int, d3 int, d4 int);",
                // nullable tables
                @"create table r (r1 int, r2 int, r3 int, r4 int);",
            };
            var stmt = RawParser.ParseSqlStatements(string.Join("", createtables));
            stmt.Exec();

            // load tables
            string curdir = Directory.GetCurrentDirectory();
            string folder = $@"{curdir}\..\..\..\data";
            foreach (var v in new List<char>(){ 'a', 'b', 'c', 'd', 'r' })
            {
                string filename = $@"'{folder}\{v}.tbl'";
                var sql = $"copy {v} from {filename};";
                var result = SQLStatement.ExecSQL(sql, out _, out _);
            }

            // create index
            string[] createindexes = {
                @"create unique index dd1 on d(d1);",
                @"create index dd2 on d(d2);",
            };
            stmt = RawParser.ParseSqlStatements(string.Join("", createindexes));
            stmt.Exec();

            // analyze tables
            foreach (var v in new List<char>() { 'a', 'b', 'c', 'd', 'r' })
            {
                var sql = $"analyze {v};";
                var result = SQLStatement.ExecSQL(sql, out _, out _);
            }
        }
        static Catalog()
        {
            // be careful: any exception happened here will be swallowed without throw any exception
            createBuildInTestTables();
            //Tpch.CreateTables();

            // customer table is dup named with tpch, so we can't load tpcds for now
            // Tpcds.CreateTables();
        }
    }
}
