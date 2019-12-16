﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Value = System.Object;

namespace adb
{
    public class SemanticExecutionException : Exception
    {
        public SemanticExecutionException(string msg) => Console.WriteLine(msg);
    }

    public class Row
    {
        public readonly List<Value> values_ = new List<Value>();

        public Row() { }
        public Row(List<Value> values) => values_ = values;

        // used by outer joins
        public Row(int nNulls) {
            Debug.Assert(nNulls > 0);
            for (int i = 0; i < nNulls; i++)
                values_.Add(null);
        }
        public Row(Row l, Row r)
        {
            // for semi/anti-semi joins, one of them may be null
            Debug.Assert(l!=null || r!=null);
            if (l != null)
                values_.AddRange(l.values_);
            if (r != null)
                values_.AddRange(r.values_);
        }

        public int ColCount() => values_.Count;

        public override string ToString() => string.Join(",", values_);
    }

    public class Parameter
    {
        public readonly TableRef tabref_;   // from which table
        public readonly Row row_;   // what's the value of parameter

        public Parameter(TableRef tabref, Row row) { tabref_ = tabref; row_ = row; }
        public override string ToString() => $"?{tabref_}.{row_}";
    }

    public class ExecContext
    {
        public List<Parameter> params_ = new List<Parameter>();

        public void Reset() { params_.Clear(); }
        public Value GetParam(TableRef tabref, int ordinal)
        {
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count == 1);
            return params_.Find(x => x.tabref_.Equals(tabref)).row_.values_[ordinal];
        }
        public void AddParam(TableRef tabref, Row row)
        {
            Debug.Assert(params_.FindAll(x => x.tabref_.Equals(tabref)).Count <= 1);
            params_.Remove(params_.Find(x => x.tabref_.Equals(tabref)));
            params_.Add(new Parameter(tabref, row));
        }
    }

    public abstract class PhysicNode : PlanNode<PhysicNode>
    {
        internal readonly LogicNode logic_;
        internal PhysicProfiling profile_;

        internal double cost_;

        protected PhysicNode(LogicNode logic) => logic_ = logic;

        public override string PrintOutput(int depth)
        {
            var r = "Output: " + string.Join(",", logic_.output_);
            logic_.output_.ForEach(x => r += ExprHelper.PrintExprWithSubqueryExpanded(x, depth));
            return r;
        }
        public override string PrintInlineDetails(int depth) => logic_.PrintInlineDetails(depth);
        public override string PrintMoreDetails(int depth) => logic_.PrintMoreDetails(depth);

        public virtual void Open() => children_.ForEach(x => x.Open());
        public virtual void Close() => children_.ForEach(x => x.Close());
        // @context is to carray parameters etc, @callback.Row is current row for processing
        public abstract void Exec(ExecContext context, Func<Row, string> callback);

        internal Row ExecProject(ExecContext context, Row input)
        {
            Row r = new Row();
            logic_.output_.ForEach(x => r.values_.Add(x.Exec(context, input)));

            return r;
        }

        public virtual double Cost() { return 10.0; }
    }

    public class PhysicMemoNode : PhysicNode
    {
        public PhysicMemoNode(LogicNode logic) : base(logic) {
            Debug.Assert(logic is LogicMemoNode);
        }
        public override string ToString() => logic_.ToString();

        public override void Exec(ExecContext context, Func<Row, string> callback) => throw new InvalidProgramException("shall not be here");
        public override int GetHashCode() => (logic_ as LogicMemoNode).group_.memoid_;
        public override bool Equals(object obj)
        {
            if (obj is PhysicMemoNode lo)
                return (lo.logic_ as LogicMemoNode).MemoLogicSign() == (logic_ as LogicMemoNode).MemoLogicSign();
            return false;
        }

        internal double MinCost() => (logic_ as LogicMemoNode).group_.MinCost();
        internal CMemoGroup Group() => (logic_ as LogicMemoNode).group_;
    }


    public class PhysicScanTable : PhysicNode
    {
        private long nrows_ = 3;
        public PhysicScanTable(LogicNode logic) : base(logic) { }
        public override string ToString() => $"PScan({(logic_ as LogicScanTable).tabref_}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanTable;
            var filter = logic.filter_;
            var tabname = logic.tabref_.relname_.ToLower();
            string[] faketable = { "a", "b", "c", "d"};
            bool isFaketable = faketable.Contains(tabname);
            var heap = (logic.tabref_).Table().heap_.GetEnumerator();

            if (!isFaketable)
                nrows_ = long.MaxValue;
            for (var i = 0; i < nrows_; i++)
            {
                Row r = new Row();
                if (isFaketable)
                {
                    r.values_.Add(i);
                    r.values_.Add(i + 1);
                    r.values_.Add(i + 2);
                    r.values_.Add(i + 3);
                }
                else
                {
                    if (heap.MoveNext())
                        r = heap.Current;
                    else
                        break;
                }

                if (logic.tabref_.outerrefs_.Count != 0)
                    context.AddParam(logic.tabref_, r);
                if (filter is null || (bool)filter.Exec(context, r))
                {
                    r = ExecProject(context, r);
                    callback(r);
                }
            }
        }
    }

    public class PhysicScanFile : PhysicNode
    {
        public PhysicScanFile(LogicNode logic) : base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicScanFile;
            var filename = logic.FileName();
            var columns = logic.tabref_.baseref_.Table().ColumnsInOrder();
            Utils.ReadCsvLine(filename, fields =>
            {
                Row r = new Row();

                int i = 0;
                Array.ForEach(fields, f =>
                {
                    switch (columns[i].type_)
                    {
                        case IntType i:
                            r.values_.Add(int.Parse(f));
                            break;
                        case DateTimeType d:
                            r.values_.Add(DateTime.Parse(f));
                            break;
                        case DoubleType b:
                            r.values_.Add(Double.Parse(f));
                            break;
                        default:
                            r.values_.Add(f);
                            break;
                    }
                    i++;
                });
                Debug.Assert(r.ColCount() == columns.Count);

                callback(r);
            });
        }
    }

    public class PhysicNLJoin : PhysicNode
    {
        public PhysicNLJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PNLJ({children_[0]},{children_[1]}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicSemiJoin);
            bool antisemi = (logic_ is LogicAntiSemiJoin);

            l_().Exec(context, l =>
            {
                bool foundOneMatch = false;
                r_().Exec(context, r =>
                {
                    if (!semi || !foundOneMatch)
                    {
                        Row n = new Row(l, r);
                        if (filter is null || (bool)filter.Exec(context, n))
                        {
                            foundOneMatch = true;
                            if (!antisemi)
                            {
                                n = ExecProject(context, n);
                                callback(n);
                            }
                        }
                    }
                    return null;
                });

                if (antisemi && !foundOneMatch)
                {
                    Row n = new Row(l, null);
                    n = ExecProject(context, n);
                    callback(n);
                }
                return null;
            });
        }

        public override double Cost()
        {
            return (l_() as PhysicMemoNode).MinCost() * (r_() as PhysicMemoNode).MinCost();
        }
    }

    public class PhysicHashJoin : PhysicNode
    {
        public PhysicHashJoin(LogicJoin logic, PhysicNode l, PhysicNode r) : base(logic)
        {
            children_.Add(l); children_.Add(r);
        }
        public override string ToString() => $"PHJ({children_[0]},{children_[1]}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicJoin;
            var filter = logic.filter_;
            bool semi = (logic_ is LogicSemiJoin);
            bool antisemi = (logic_ is LogicAntiSemiJoin);

            l_().Exec(context, l =>
            {
                bool foundOneMatch = false;
                r_().Exec(context, r =>
                {
                    if (!semi || !foundOneMatch)
                    {
                        Row n = new Row(l, r);
                        if (filter is null || (bool)filter.Exec(context, n))
                        {
                            foundOneMatch = true;
                            if (!antisemi)
                            {
                                n = ExecProject(context, n);
                                callback(n);
                            }
                        }
                    }
                    return null;
                });

                if (antisemi && !foundOneMatch)
                {
                    Row n = new Row(l, null);
                    n = ExecProject(context, n);
                    callback(n);
                }
                return null;
            });
        }

        public override double Cost()
        {
            return (l_() as PhysicMemoNode).MinCost() + (r_() as PhysicMemoNode).MinCost();
        }
    }

    public class PhysicHashAgg : PhysicNode
    {
        private class KeyList
        {
            internal List<Value> keys_ = new List<Value>();

            static internal KeyList ComputeKeys(ExecContext context, LogicAgg agg, Row input)
            {
                var list = new KeyList();
                if (agg.keys_ != null)
                    agg.keys_.ForEach(x => list.keys_.Add(x.Exec(context, input)));
                return list;
            }

            public override string ToString() => string.Join(",", keys_);
            public override int GetHashCode()
            {
                int hashcode = 0;
                keys_.ForEach(x => hashcode ^= x.GetHashCode());
                return hashcode;
            }
            public override bool Equals(object obj)
            {
                var keyl = obj as KeyList;
                Debug.Assert(obj is KeyList);
                Debug.Assert(keyl.keys_.Count == keys_.Count);
                return keys_.SequenceEqual(keyl.keys_);
            }
        };
        public PhysicHashAgg(LogicAgg logic, PhysicNode l) : base(logic) => children_.Add(l);
        public override string ToString() => $"PHAgg({(logic_ as LogicAgg)}: {Cost()})";

        private Row AggrCoreToRow(ExecContext context, Row input)
        {
            Row r = new Row();
            (logic_ as LogicAgg).aggrCore_.ForEach(x => r.values_.Add(x.Exec(context, input)));
            return r;
        }
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicAgg;
            var aggrcore = logic.aggrCore_;
            var hm = new Dictionary<KeyList, Row>();

            // aggregation is working on aggCore targets
            child_().Exec(context, l =>
            {
                var keys = KeyList.ComputeKeys(context, logic, l);

                if (hm.TryGetValue(keys, out Row exist))
                {
                    aggrcore.ForEach(x =>
                    {
                        var xa = x as AggFunc;
                        var old = exist.values_[aggrcore.IndexOf(xa)];
                        xa.Accum(context, old, l);
                    });

                    hm[keys] = AggrCoreToRow(context, l);
                }
                else
                {
                    aggrcore.ForEach(x =>
                    {
                        var xa = x as AggFunc;
                        xa.Init(context, l);
                    });

                    hm.Add(keys, AggrCoreToRow(context, l));
                }
                return null;
            });

            // stitch keys+aggcore into final output
            foreach (var v in hm)
            {
                var w = new Row(new Row(v.Key.keys_), v.Value);
                var newr = ExecProject(context, w);
                callback(newr);
            }
        }
    }

    public class PhysicOrder : PhysicNode
    {
        public PhysicOrder(LogicOrder logic, PhysicNode l) : base(logic) => children_.Add(l);

        // respect logic.orders_|descends_
        private int compareRow(Row l, Row r)
        {
            var logic = logic_ as LogicOrder;
            var orders = logic.orders_;
            var descends = logic.descends_;
            return 0;
        }
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicOrder;
            var set = new List<Row>();
            child_().Exec(context, l =>
            {
                set.Add(l);
                return null;
            });
            set.Sort(compareRow);

            // output sorted set
            foreach (var v in set)
                callback(v);
        }
    }

    public class PhysicFromQuery : PhysicNode
    {
        public PhysicFromQuery(LogicFromQuery logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            var logic = logic_ as LogicFromQuery;
            child_().Exec(context, l =>
            {
                if (logic.queryRef_.outerrefs_.Count != 0)
                    context.AddParam(logic.queryRef_, l);
                var r = ExecProject(context, l);
                callback(r);
                return null;
            });
        }
    }

    // this class shall be removed after filter associated with each node
    public class PhysicFilter : PhysicNode
    {
        public PhysicFilter(LogicFilter logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override string ToString() => $"PFILTER({children_[0]}: {Cost()})";

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            Expr filter = (logic_ as LogicFilter).filter_;

            child_().Exec(context, l =>
            {
                if (filter is null || (bool)filter.Exec(context, l))
                {
                    var r = ExecProject(context, l);
                    callback(r);
                }
                return null;
            });
        }
        public override int GetHashCode()
        {
            Expr filter = (logic_ as LogicFilter).filter_;
            return base.GetHashCode() ^ (filter?.GetHashCode() ?? 0);
        }
        public override bool Equals(object obj)
        {
            Expr filter = (logic_ as LogicFilter).filter_;
            if (obj is PhysicFilter lo)
            {
                return base.Equals(lo) && (filter?.Equals((lo.logic_ as LogicFilter)?.filter_) ?? true);
            }
            return false;
        }
    }

    public class PhysicInsert : PhysicNode
    {
        public PhysicInsert(LogicInsert logic, PhysicNode l) : base(logic) => children_.Add(l);

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, l =>
            {
                var table = (logic_ as LogicInsert).targetref_.Table();
                table.heap_.Add(l);
                return null;
            });
        }
    }

    public class PhysicResult : PhysicNode
    {
        public PhysicResult(LogicResult logic) : base(logic) { }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            Row r = ExecProject(context, null);
            callback(r);
        }
    }

    public class PhysicProfiling : PhysicNode
    {
        internal Int64 nrows_;

        public PhysicProfiling(PhysicNode l) : base(l.logic_)
        {
            children_.Add(l);
            l.profile_ = this;
            Debug.Assert(profile_ is null);
        }

        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            child_().Exec(context, l =>
            {
                nrows_++;
                callback(l);
                return null;
            });
        }
    }

    public class PhysicCollect : PhysicNode
    {
        public readonly List<Row> rows_ = new List<Row>();

        public PhysicCollect(PhysicNode child) : base(null) => children_.Add(child);
        public override void Exec(ExecContext context, Func<Row, string> callback)
        {
            context.Reset();
            child_().Exec(context, r =>
            {
                Row newr = new Row();
                var child = (children_[0] is PhysicProfiling) ?
                        children_[0].children_[0] : children_[0];
                List<Expr> output = child.logic_.output_;
                for (int i = 0; i < output.Count; i++)
                {
                    if (output[i].isVisible_)
                        newr.values_.Add(r.values_[i]);
                }
                rows_.Add(newr);
                Console.WriteLine($"{newr}");
                return null;
            });
        }
    }
}
