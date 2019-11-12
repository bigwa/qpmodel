﻿using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace adb
{
    public static class Utils
    {
        internal static string Tabs(int depth) => new string(' ', depth * 2);

        // this is shortcut for unhandled conditions - they shall be translated to 
        // related exceptional handling code later
        //
        public static void Checks(bool cond) => Debug.Assert(cond);
        public static void Assumes(bool cond) => Debug.Assert(cond);
        public static void Checks(bool cond, string message) => Debug.Assert(cond, message);
        public static void Assumes(bool cond, string message) => Debug.Assert(cond, message);

        // a contains b?
        public static bool ListAContainsB<T>(List<T> a, List<T> b) => !b.Except(a).Any();

        // for each element in @source, if there is a matching k in @target of its sub expression, 
        // replace that element as ExprRef(k, index_of_k_in_target)
        //
        public static List<Expr> SearchReplace(List<Expr> source, List<Expr> target)
        {
            var r = new List<Expr>();
            source.ForEach(x =>
            {
                for (int i = 0; i < target.Count; i++)
                {
                    var e = target[i];
                    x = x.SearchReplace(e, new ExprRef(e, i));
                }
                r.Add(x);
            });

            Debug.Assert(r.Count == source.Count);
            return r;
        }

        // a[0]+b[1] => a+b 
        public static string RemovePositions(string r)
        {
            do
            {
                int start = r.IndexOf('[');
                if (start == -1)
                    break;
                int end = r.IndexOf(']');
                Debug.Assert(end != -1);
                var middle = r.Substring(start + 1, end - start - 1);
                Debug.Assert(int.TryParse(middle, out int result));
                r = r.Replace($"[{middle}]", "");
            } while (r.Length > 0);
            return r;
        }

        public static void ReadCsvLine(string filepath, Action<string[]> action)
        {
            using var parser = new TextFieldParser(filepath);
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters("|");
            while (!parser.EndOfData)
            {
                //Processing row
                string[] fields = parser.ReadFields();
                action(fields);
            }
        }
    }
}
