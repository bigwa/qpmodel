﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace adb
{
    public abstract class LogicNode : PlanNode<LogicNode>
    {
        // TODO: we can consider normalize all node specific expressions into List<Expr> 
        // so processing can be generalized - similar to Expr.children_[]
        //
        public Expr filter_ = null;
        public List<Expr> output_ = new List<Expr>();

        public override string PrintMoreDetails(int depth) => PrintFilter(filter_, depth);

        public override string PrintOutput(int depth)
        {
            if (output_.Count != 0)
            {
                string r = "Output: " + string.Join(",", output_);
                output_.ForEach(x => r += x.PrintExprWithSubqueryExpanded(depth));
                return r;
            }
            return null;
        }

        // This is an honest translation from logic to physical plan
        public PhysicNode DirectToPhysical(ProfileOption profiling)
        {
            PhysicNode root = null;
            TraversEachNode(n =>
            {
                PhysicNode phy;
                switch (n)
                {
                    case LogicScanTable ln:
                        // if there are indexes can help filter, use them
                        if (ln.filter_.FilterCanUseIndex(ln.tabref_))
                           phy = new PhysicSeekIndex(ln);
                        else
                            phy = new PhysicScanTable(ln);
                        if (ln.filter_ != null)
                            ln.filter_.SubqueryDirectToPhysic();
                        break;
                    case LogicJoin lc:
                        var l = n.l_();
                        var r = n.r_();
                        switch (lc)
                        {
                            case LogicSingleMarkJoin lsmj:
                                phy = new PhysicSingleMarkJoin(lsmj,
                                    l.DirectToPhysical(profiling),
                                    r.DirectToPhysical(profiling));
                                break;
                            case LogicMarkJoin lmj:
                                phy = new PhysicMarkJoin(lmj,
                                    l.DirectToPhysical(profiling),
                                    r.DirectToPhysical(profiling));
                                break;
                            case LogicSingleJoin lsj:
                                phy = new PhysicSingleJoin(lsj,
                                    l.DirectToPhysical(profiling),
                                    r.DirectToPhysical(profiling));
                                break;
                            default:
                                bool lhasouter = TableRef.HasOuterRefs(l.InclusiveTableRefs());
                                bool rhasouter = TableRef.HasOuterRefs(r.InclusiveTableRefs());

                                // if left side has outerrefs, we needs NLJ to drive the parameter
                                // pass to right side, so we can't use HJ in this case.
                                if (lc.filter_.FilterHashable() && !lhasouter)
                                    phy = new PhysicHashJoin(lc,
                                        l.DirectToPhysical(profiling),
                                        r.DirectToPhysical(profiling));
                                else
                                {
                                    // it is ok right side reference left side's variable but not 
                                    // the other direction. In this case, we shall flip the side.
                                    //
                                    phy = new PhysicNLJoin(lc,
                                        l.DirectToPhysical(profiling),
                                        r.DirectToPhysical(profiling));
                                }
                                break;
                        }
                        break;
                    case LogicResult lr:
                        phy = new PhysicResult(lr);
                        break;
                    case LogicFromQuery ls:
                        phy = new PhysicFromQuery(ls, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicFilter lf:
                        phy = new PhysicFilter(lf, n.child_().DirectToPhysical(profiling));
                        if (lf.filter_ != null)
                            lf.filter_.SubqueryDirectToPhysic();
                        break;
                    case LogicInsert li:
                        phy = new PhysicInsert(li, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicScanFile le:
                        phy = new PhysicScanFile(le);
                        break;
                    case LogicAgg la:
                        phy = new PhysicHashAgg(la, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicOrder lo:
                        phy = new PhysicOrder(lo, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicAnalyze lan:
                        phy = new PhysicAnalyze(lan, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicIndex lindex:
                        phy = new PhysicIndex(lindex, n.child_().DirectToPhysical(profiling));
                        break;
                    case LogicLimit limit:
                        phy = new PhysicLimit(limit, n.child_().DirectToPhysical(profiling));
                        break;
                    default:
                        throw new NotImplementedException();
                }

                if (profiling.enabled_)
                    phy = new PhysicProfiling(phy);

                if (root is null)
                    root = phy;
            });

            return root;
        }

        public List<TableRef> InclusiveTableRefs()
        {
            List<TableRef> refs = new List<TableRef>();
            TraversEachNode(x =>
            {
                if (x is LogicScanTable gx)
                    refs.Add(gx.tabref_);
                else if (x is LogicFromQuery fx)
                {
                    refs.Add(fx.queryRef_);
                    refs.AddRange(fx.queryRef_.query_.bindContext_.AllTableRefs());
                }
            });
            return refs;
        }

        internal Expr CloneFixColumnOrdinal(Expr toclone, List<Expr> source)
        {
            var clone = toclone.Clone();

            // first try to match the whole expression - don't do this for ColExpr
            // because it has no practial benefits.
            // 
            if (!(clone is ColExpr))
            {
                int ordinal = source.FindIndex(clone.Equals);
                if (ordinal != -1)
                    return new ExprRef(clone, ordinal);
            }

            // we have to use each ColExpr and fix its ordinal
            clone.VisitEach<ColExpr>(target =>
            {
                // ignore system column
                if (target is SysColExpr)
                    return;

                Predicate<Expr> nameTest;
                nameTest = z => target.Equals(z) || 
                            target.colName_.Equals(z.outputName_);

                // using source's matching index for ordinal
                // fix colexpr's ordinal - leave the outerref as it is already handled in ColExpr.Bind()
                if (!target.isOuterRef_)
                {
                    target.ordinal_ = source.FindIndex(nameTest);

                    // we may hit more than one target, say t2.col1 matching {t1.col1, t2.col1}
                    // in this case, we shall redo the mapping with table name
                    //
                    Debug.Assert (source.FindAll(nameTest).Count >= 1);
                    if (source.FindAll(nameTest).Count > 1) {
                        nameTest = z => z is ColExpr 
                                && target.colName_.Equals(z.outputName_)
                                && target.tabRef_.Equals((z as ColExpr).tabRef_);
                        target.ordinal_ = source.FindIndex(nameTest);
                        Debug.Assert(source.FindAll(nameTest).Count == 1);
                    }
                }
                Debug.Assert(target.ordinal_ != -1);
            });

            return clone;
        }

        // fix each expression by using source's ordinal and make a copy
        internal List<Expr> CloneFixColumnOrdinal(List<Expr> toclone, List<Expr> source, bool removeRedundant)
        {
            var clone = new List<Expr>();
            toclone.ForEach(x => clone.Add(CloneFixColumnOrdinal(x, source)));
            Debug.Assert(clone.Count == toclone.Count);

            if (removeRedundant)
                return clone.Distinct().ToList();
            return clone;
        }

        public virtual int MemoLogicSign() => GetHashCode();

        // resolve mapping from children output
        // 1. you shall first compute the reqOutput by accouting parent's reqOutput and your filter etc
        // 2. compute children's output by requesting reqOutput from them
        // 3. find mapping from children's output
        //
        public virtual List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true) => null;

        public virtual long EstCardinality() {
            long card = 1;
            children_.ForEach(x => card = Math.Max(x.EstCardinality(), card));
            return card;
        }
    }

    // LogicMemoRef wrap a CMemoGroup as a LogicNode (so CMemoGroup can be used in plan tree)
    //
    public class LogicMemoRef : LogicNode {
        public CMemoGroup group_;

        public LogicNode Deref() => child_();
        public T Deref<T>() where T: LogicNode => (T)Deref();

        public LogicMemoRef(CMemoGroup group)
        {
            Debug.Assert(group != null);
            var child = group.exprList_[0].logic_;

            children_.Add(child);
            group_ = group;

            Debug.Assert(filter_ is null);
            Debug.Assert(!(Deref() is LogicMemoRef));
            Debug.Assert(group.memo_.LookupCGroup(Deref()) == group);
        }
        public override string ToString() => group_.ToString();

        public override int MemoLogicSign() => Deref().MemoLogicSign();
        public override int GetHashCode() => MemoLogicSign();
        public override bool Equals(object obj) 
        {
            if (obj is LogicMemoRef lo)
                return lo.MemoLogicSign() == MemoLogicSign();
            return false;
        }
    }

    enum JoinType {
        // ANSI SQL specified join types can show in SQL statement
        InnerJoin,
        LeftJoin,
        RightJoin,
        FullJoin,
        CrossJoin
            ,
        // these are used by subquery expansion or optimizations (say PK/FK join)
        SemiJoin,
        AntiSemiJoin,
    };

    public class LogicJoin : LogicNode
    {
        internal JoinType type_ = JoinType.InnerJoin;
        public override string ToString() => $"({l_()} {type_} {r_()})";
        public LogicJoin(LogicNode l, LogicNode r) { children_.Add(l); children_.Add(r); }
        public LogicJoin(LogicNode l, LogicNode r, Expr filter): this(l, r) 
        { 
            filter_ = filter; 
        }

        public override int MemoLogicSign() {
            var filterhash = 0;
            if (filter_ != null) {
                // consider the case:
                //   A X (B X C on f3) on f1 AND f2
                // is equal to commutative transformation
                //   (A X B on f1) X C on f3 AND f2
                // The filter signature generation has to be able to accomomdate this difference.
                //
                var andlist = filter_.FilterToAndList();
                filterhash = andlist.ListHashCode();
                //filterhash = filter_.GetHashCode();
            }
            return l_().MemoLogicSign() ^ r_().MemoLogicSign() ^ filterhash;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ (filter_?.GetHashCode() ?? 0);
        }
        public override bool Equals(object obj)
        {
            if (obj is LogicJoin lo) {
                return base.Equals(lo) && (filter_?.Equals(lo.filter_) ?? true);
            }
            return false;
        }

        public bool AddFilter(Expr filter)
        {
            filter_ = filter_.AddAndFilter(filter);
            return true;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>(reqOutput);
            if (filter_ != null)
                reqFromChild.Add(filter_);

            // push to left and right: to which side depends on the TableRef it contains
            var ltables = l_().InclusiveTableRefs();
            var rtables = r_().InclusiveTableRefs();
            var lreq = new HashSet<Expr>();
            var rreq = new HashSet<Expr>();
            foreach (var v in reqFromChild)
            {
                var tables = v.AllTableRef();

                if (ltables.ContainsList( tables))
                    lreq.Add(v);
                else if (rtables.ContainsList( tables))
                    rreq.Add(v);
                else
                {
                    // the whole list can't push to the children (Eg. a.a1 + b.b1)
                    // decompose to singleton and push down
                    var colref = v.RetrieveAllColExpr();
                    colref.ForEach(x =>
                    {
                        if (ltables.Contains(x.tabRef_))
                            lreq.Add(x);
                        else if (rtables.Contains(x.tabRef_))
                            rreq.Add(x);
                        else
                            throw new InvalidProgramException("contains invalid tableref");
                    });
                }
            }

            // get left and right child to resolve columns
            l_().ResolveColumnOrdinal(lreq.ToList());
            var lout = l_().output_;
            r_().ResolveColumnOrdinal(rreq.ToList());
            var rout = r_().output_;
            Debug.Assert(lout.Intersect(rout).Count() == 0);

            // assuming left output first followed with right output
            var childrenout = lout.ToList(); childrenout.AddRange(rout.ToList());
            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, childrenout);
            output_ = CloneFixColumnOrdinal(reqOutput, childrenout, removeRedundant);
            return ordinals;
        }
    }

    public class LogicFilter : LogicNode
    {
        public override string ToString() => $"{child_()} filter: {filter_}";

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ filter_.GetHashCode();
        }
        public override bool Equals(object obj)
        {
            if (obj is LogicFilter lo)
            {
                return base.Equals(lo) && filter_.Equals(lo.filter_);
            }
            return false;
        }

        public LogicFilter(LogicNode child, Expr filter)
        {
            children_.Add(child); filter_ = filter;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            // request from child including reqOutput and filter
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());
            reqFromChild.AddRange(filter_.RetrieveAllColExpr());
            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;

            filter_ = CloneFixColumnOrdinal(filter_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);
            return ordinals;
        }
    }

    public class LogicAgg : LogicNode
    {
        internal List<Expr> keys_;
        internal Expr having_;

        // runtime info: derived from output request
        internal List<AggFunc> aggrFns_ = new List<AggFunc>();
        public override string ToString() => $"Agg({child_()})";

        public override string PrintMoreDetails(int depth)
        {
            string r = null;
            if (aggrFns_.Count > 0)
                r += $"Aggregates: {string.Join(", ", aggrFns_)}\n{Utils.Tabs(depth + 2)}";
            if (keys_ != null)
                r += $"Group by: {string.Join(", ", keys_)}\n";
            if (having_ != null)
                r += Utils.Tabs(depth + 2) + $"{PrintFilter(having_, depth)}";
            return r;
        }

        public LogicAgg(LogicNode child, List<Expr> groupby, List<Expr> aggrs, Expr having)
        {
            children_.Add(child); keys_ = groupby; having_ = having;
        }

        // key: b1+b2
        // x: b1+b2+3 => true, b1+b2 => false
        bool exprConsistPureKeys(Expr x, List<Expr> keys)
        {
            var constTrue = new LiteralExpr("true", new BoolType());
            if (keys is null)
                return false;
            if (keys.Contains(x))
                return false;

            Expr xchanged = x.Clone();
            foreach (var v in keys)
                xchanged = xchanged.SearchReplace(v, constTrue);
            if (!xchanged.VisitEachExprExists(
                    e => e.IsLeaf() && !(e is LiteralExpr) && !e.Equals(constTrue)))
                return true;
            return false;
        }

        // say 
        //  keys: k1+k2,k2+k3 
        //  reqOutput: (k1+k2)+(k3+k2), count(*), sum(count(a1)*b1) 
        //  => 0, a1, b1
        //
        List<Expr> removeAggFuncAndKeyExprsFromOutput(List<Expr> reqList, List<Expr> keys)
        {
            var reqContainAggs = new List<Expr>();

            // first let's find out all elements containing any AggFunc
            reqList.ForEach(x =>
                x.VisitEach<AggFunc>(y =>
                {
                    // 1+abs(min(a))+max(b)
                    reqContainAggs.Add(x);
                }));

            // we also remove expressions only with key computations - this non-trivial
            // consider this:
            //    keys: a1+a2, a2+a3
            //    expr: (a1+a2+a2)+a3, sum(a+a2+(a3+a2)), 
            //  with some arrangement, we can map expr to keys
            //
            if (keys?.Count > 0)
            {
                var constTrue = new LiteralExpr("true", new BoolType());
                reqList.ForEach(x =>
                {
                    if (reqContainAggs.Contains(x))
                        return;
                    if (exprConsistPureKeys(x, keys))
                        reqContainAggs.Add(x);
                });
            }

            // now remove AggFunc but add back the dependent exprs
            reqContainAggs.ForEach(x =>
            {
                // remove the element from reqList
                reqList.Remove(x);

                // add back the dependent exprs back
                x.VisitEach<AggFunc>(y =>
                {
                    foreach (var z in y.GetNonFuncExprList())
                    {
                        if (!exprConsistPureKeys(y, keys))
                            reqList.Add(z);
                    }
                });
            });

            reqList = reqList.Distinct().ToList();
            reqList.ForEach(x => Debug.Assert(!x.HasAggFunc()));
            return reqList;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();

            // reqOutput may contain ExprRef which is generated during FromQuery removal process, remove them
            var reqList = reqOutput.CloneList(new List<Type> { typeof(LiteralExpr)});

            // request from child including reqOutput and filter. Don't use whole expression
            // matching push down like k+k => (k+k)[0] instead, we need k[0]+k[0] because 
            // aggregation only projection values from hash table(key, value).
            //
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(removeAggFuncAndKeyExprsFromOutput(reqList, keys_));

            // It is ideal to add keys_ directly to reqFromChild but matching can be harder.
            // Consider the following case:
            //   keys/having: a1+a2, a3+a4
            //   reqOutput: a1+a3+a2+a4
            // Let's fix this later
            //
            if (keys_ != null)
                reqFromChild.AddRange(keys_.RetrieveAllColExpr());
            if (having_ != null)
                reqFromChild.AddRange(having_.RetrieveAllColExpr());
            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;

            if (keys_ != null) 
                keys_ = CloneFixColumnOrdinal(keys_, childout, true);
            if (having_ != null)
                having_ = CloneFixColumnOrdinal(having_, childout);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);

            // Bound aggrs to output, so when we computed aggrs, we automatically get output
            // Here is an example:
            //  output_: <literal>, cos(a1*7)+sum(a1),  sum(a1) + sum(a2+a3)*2
            //                       |           \       /          |   
            //                       |            \     /           |   
            //  keys_:               a1            \   /            |
            //  aggrFns_:                        sum(a1),      sum(a2+a3)
            // =>
            //  output_: <literal>, cos(ref[0]*7)+ref[1],  ref[1]+ref[2]*2
            //
            var nkeys = keys_?.Count ?? 0;
            var newoutput = new List<Expr>();
            if (keys_ != null) output_ = output_.SearchReplace(keys_);
            output_.ForEach(x =>
            {
                x.VisitEach<AggFunc>(y =>
                {
                    // remove the duplicates immediatley to avoid wrong ordinal in ExprRef
                    if (!aggrFns_.Contains(y))
                        aggrFns_.Add(y);
                    x = x.SearchReplace(y, new ExprRef(y, nkeys + aggrFns_.IndexOf(y)));
                });

                newoutput.Add(x);
            });
            if (having_ != null) having_ = having_.SearchReplace(keys_);
            having_?.VisitEach<AggFunc>(y =>
            {
                // remove the duplicates immediatley to avoid wrong ordinal in ExprRef
                if (!aggrFns_.Contains(y))
                    aggrFns_.Add(y);
                having_ = having_.SearchReplace(y, new ExprRef(y, nkeys + aggrFns_.IndexOf(y)));
            });
            Debug.Assert(aggrFns_.Count == aggrFns_.Distinct().Count());

            // Say invvalid expression means contains colexpr (replaced with ref), then the output shall
            // contains no expression consists invalid expression
            //
            Expr offending = null;
            newoutput.ForEach(x =>
            {
                if (x.VisitEachExprExists(y => y is ColExpr, new List<Type> { typeof(ExprRef) }))
                    offending = x;
            });
            if (offending != null)
                throw new SemanticAnalyzeException($"column {offending} must appear in group by clause");
            output_ = newoutput;

            return ordinals;
        }

    }

    public class LogicOrder : LogicNode
    {
        internal List<Expr> orders_ = new List<Expr>();
        internal List<bool> descends_ = new List<bool>();
        public override string PrintMoreDetails(int depth)
        {
            var r = $"Order by: {string.Join(", ", orders_)}\n";
            return r;
        }

        public LogicOrder(LogicNode child, List<Expr> orders, List<bool> descends)
        {
            children_.Add(child);
            orders_ = orders;
            descends_ = descends;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // request from child including reqOutput and filter
            List<int> ordinals = new List<int>();
            List<Expr> reqFromChild = new List<Expr>();
            reqFromChild.AddRange(reqOutput.CloneList());
            reqFromChild.AddRange(orders_);
            child_().ResolveColumnOrdinal(reqFromChild);
            var childout = child_().output_;

            orders_ = CloneFixColumnOrdinal(orders_, childout, false);
            output_ = CloneFixColumnOrdinal(reqOutput, childout, removeRedundant);
            return ordinals;
        }
    }

    public class LogicFromQuery : LogicNode
    {
        public QueryRef queryRef_;

        public override string ToString() => $"<{queryRef_.alias_}>({child_()})";
        public override string PrintInlineDetails(int depth) => $"<{queryRef_.alias_}>";
        public LogicFromQuery(QueryRef query, LogicNode child) { queryRef_ = query; children_.Add(child); }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            var query = queryRef_.query_;
            query.logicPlan_.ResolveColumnOrdinal(query.selection_);

            var childout = queryRef_.AllColumnsRefs();
            output_ = CloneFixColumnOrdinal(reqOutput, childout, false);

            // finally, consider outerref to this table: if it is not there, add it. We can't
            // simply remove redundant because we have to respect removeRedundant flag
            //
            output_ = queryRef_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicGet<T> : LogicNode where T : TableRef
    {
        public T tabref_;

        public LogicGet(T tab) => tabref_ = tab;
        public override string ToString() => tabref_.ToString();
        public override string PrintInlineDetails(int depth) => ToString();
        public override int GetHashCode() => base.GetHashCode() ^ (filter_?.GetHashCode()??0) ^ tabref_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is LogicGet<T> lo)
                return base.Equals(lo) && (filter_?.Equals(lo.filter_)??true) && tabref_.Equals(lo.tabref_);
            return false;
        }

        public bool AddFilter(Expr filter)
        {
            filter_ = filter_.AddAndFilter(filter);
            return true;
        }

        void validateReqOutput(List<Expr> reqOutput)
        {
            reqOutput.ForEach(x =>
            {
                x.VisitEachExpr(y =>
                {
                    switch (y)
                    {
                        case LiteralExpr ly:    // select 2+3, ...
                        case SubqueryExpr sy:   // select ..., sx = (select b1 from b limit 1) from a;
                            break;
                        default:
                            // aggfunc shall never pushed to me
                            Debug.Assert(!(y is AggFunc));

                            // a single table column ref, or combination of them say "c1+c2+7"
                            Debug.Assert(!y.IsConst());
                            Debug.Assert(y.EqualTableRef(tabref_));
                            break;
                    }
                });
            });
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            List<int> ordinals = new List<int>();
            List<Expr> columns = tabref_.AllColumnsRefs();

            // Verify it can be an litral, or only uses my tableref
            validateReqOutput(reqOutput);

            if (filter_ != null)
                filter_ = CloneFixColumnOrdinal(filter_, columns);
            output_ = CloneFixColumnOrdinal(reqOutput, columns, false);

            // Finally, consider outerrefs to this table: if they are not there, add them
            output_ = tabref_.AddOuterRefsToOutput(output_);
            if (removeRedundant)
                output_ = output_.Distinct().ToList();
            return ordinals;
        }
    }

    public class LogicScanTable : LogicGet<BaseTableRef>
    {
        public LogicScanTable(BaseTableRef tab) : base(tab) { }
        public override long EstCardinality()
        {
            return Math.Max(3, Catalog.sysstat_.NumberOfRows(tabref_.relname_));
        }
    }

    public class LogicScanFile : LogicGet<ExternalTableRef>
    {
        public string FileName() => tabref_.filename_;
        public LogicScanFile(ExternalTableRef tab) : base(tab) { }
    }

    public class LogicInsert : LogicNode
    {
        public BaseTableRef targetref_;
        public LogicInsert(BaseTableRef targetref, LogicNode child)
        {
            targetref_ = targetref;
            children_.Add(child);
        }
        public override string ToString() => targetref_.ToString();
        public override string PrintInlineDetails(int depth) => ToString();

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            Debug.Assert(output_.Count == 0);

            // insertion is always the top node 
            Debug.Assert(!removeRedundant);
            return children_[0].ResolveColumnOrdinal(reqOutput, removeRedundant);
        }
    }

    public class LogicResult : LogicNode
    {
        public override string ToString() => string.Join(",", output_);
        public LogicResult(List<Expr> exprs) => output_ = exprs;
    }

    public class LogicLimit : LogicNode {

        internal int limit_;

        public override string PrintInlineDetails(int depth) => $"({limit_})";

        public LogicLimit(LogicNode child, Expr expr)
        {
            children_.Add(child);

            Utils.Assumes(expr.IsConst());
            expr.TryEvalConst(out Object val);
            limit_ = (int)val;
        }

        public override List<int> ResolveColumnOrdinal(in List<Expr> reqOutput, bool removeRedundant = true)
        {
            // limit is the top node and don't remove redundant
            List<int> ordinals = new List<int>();

            child_().ResolveColumnOrdinal(reqOutput, false);
            output_ = child_().output_;
            return ordinals;
        }
    }
}
