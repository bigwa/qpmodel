﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Value = System.Object;

using adb.logic;
using adb.physic;
using adb.utils;

namespace adb.expr
{
    public abstract class SubqueryExpr : Expr
    {
        internal SelectStmt query_;
        internal int subqueryid_;    // bounded

        // runtime optimization for non-correlated subquery
        internal Value isCacheable_ = null;
        internal bool cachedValSet_ = false;
        internal Value cachedVal_;

        public SubqueryExpr(SelectStmt query)
        {
            query_ = query;
        }

        // don't print the subquery here, it shall be printed by up caller layer for pretty format
        public override string ToString() => $@"@{subqueryid_}";

        protected void bindQuery(BindContext context)
        {
            // subquery id is global, so accumulating at top
            subqueryid_ = ++BindContext.globalSubqCounter_;

            // query will use a new query context inside
            var mycontext = query_.Bind(context);
            Debug.Assert(query_.parent_ == mycontext.parent_?.stmt_);

            // verify column count after bound because SelStar expansion
            if (!(this is ScalarSubqueryExpr))
            {
                type_ = new BoolType();
            }
            else
            {
                if (query_.selection_.Count != 1)
                    throw new SemanticAnalyzeException("subquery must return only one column");
                type_ = query_.selection_[0].type_;
            }
        }

        public bool IsCorrelated() {
            Debug.Assert(bounded_);
            return query_.isCorrelated_;
        }

        // similar to IsCorrelated() but also consider children. If None is correlated
        // or the correlation does not go outside this expr range, then we can cache
        // the result and reuse it without repeatly execute it.
        //
        // Eg. ... a where a1 in ( ... b where exists (select * from c where c1>=a1))
        // InSubquery ... b is not correlated but its child is correlated to outside 
        // table a, which makes it not cacheable.
        //
        public bool IsCacheable() {
            if (isCacheable_ is null)
            {
                if (IsCorrelated())
                    isCacheable_ = false;
                else
                {
                    // collect all subquries within this query, they are ok to correlate
                    var queriesOkToRef = query_.InclusiveAllSubquries();

                    // if the subquery reference anything beyond the ok-range, we can't cache
                    bool childCorrelated = false;
                    query_.subQueries_.ForEach(x =>
                    {
                        if (x.isCorrelated_) { 
                            if (!queriesOkToRef.ContainsList(x.correlatedWhich_))
                                childCorrelated = true;
                        }
                    });

                    isCacheable_ = !childCorrelated;
                }
            }

            return (bool)isCacheable_;
        }

        public override int GetHashCode() => subqueryid_.GetHashCode();
        public override bool Equals(object obj)
        {
            if (obj is SubqueryExpr os)
                return os.subqueryid_ == subqueryid_;
            return false;
        }
    }

    public class ExistSubqueryExpr : SubqueryExpr
    {
        internal bool hasNot_ = false;

        public ExistSubqueryExpr(SelectStmt query) : base(query) { }

        public override void Bind(BindContext context)
        {
            bindQuery(context);
            type_ = new BoolType();
            markBounded();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            if (IsCacheable() && cachedValSet_)
                return cachedVal_;

            Row r = null;
            query_.physicPlan_.Exec(l =>
            {
                // exists check can immediately return after receiving a row
                r = l;
                return null;
            });

            bool exists = r != null;
            cachedVal_ = hasNot_? !exists: exists;
            cachedValSet_ = true;
            return cachedVal_;
        }
    }

    public class ScalarSubqueryExpr : SubqueryExpr
    {
        public ScalarSubqueryExpr(SelectStmt query) : base(query) { }

        public override void Bind(BindContext context)
        {
            bindQuery(context);
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            type_ = query_.selection_[0].type_;
            markBounded();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            if (IsCacheable() && cachedValSet_)
                return cachedVal_;

            context.option_.PushCodeGenDisable();
            Row r = null;
            query_.physicPlan_.Exec(l =>
            {
                // exists check can immediately return after receiving a row
                var prevr = r; r = l;
                if (prevr != null)
                    throw new SemanticExecutionException("subquery more than one row returned");
                return null;
            });
            context.option_.PopCodeGen();

            cachedVal_ = (r != null)? r[0] : null;
            cachedValSet_ = true;
            return cachedVal_;
        }
    }

    public class InSubqueryExpr : SubqueryExpr
    {
        // children_[0] is the expr of in-query
        internal Expr expr_() => children_[0];

        public override string ToString() => $"{expr_()} in @{subqueryid_}";
        public InSubqueryExpr(Expr expr, SelectStmt query) : base(query) { children_.Add(expr); }

        public override void Bind(BindContext context)
        {
            expr_().Bind(context);
            bindQuery(context);
            if (query_.selection_.Count != 1)
                throw new SemanticAnalyzeException("subquery must return only one column");
            type_ = new BoolType();
            markBounded();
        }

        public override Value Exec(ExecContext context, Row input)
        {
            Debug.Assert(type_ != null);
            Value expr = expr_().Exec(context, input);
            if (IsCacheable () && cachedValSet_)
                return (cachedVal_ as HashSet<Value>).Contains(expr);
            
            var set = new HashSet<Value>();
            query_.physicPlan_.Exec(l =>
            {
                // it may have hidden columns but that's after [0]
                set.Add(l[0]);
                return null;
            });

            cachedVal_ = set;
            cachedValSet_ = true;
            return set.Contains(expr); ;
        }
    }

    // In List can be varaibles:
    //      select* from a where a1 in (1, 2, a2);
    //
    public class InListExpr : Expr
    {
        internal Expr expr_() => children_[0];
        internal List<Expr> inlist_() => children_.GetRange(1, children_.Count - 1);
        public InListExpr(Expr expr, List<Expr> inlist)
        {
            children_.Add(expr); children_.AddRange(inlist);
            type_ = new BoolType();
            Debug.Assert(Clone().Equals(this));
        }

        public override int GetHashCode()
        {
            return expr_().GetHashCode() ^ inlist_().ListHashCode();
        }
        public override bool Equals(object obj)
        {
            if (obj is ExprRef or)
                return Equals(or.expr_());
            else if (obj is InListExpr co)
                return expr_().Equals(co.expr_()) && exprEquals(inlist_(), co.inlist_());
            return false;
        }

        public override Value Exec(ExecContext context, Row input)
        {
            var v = expr_().Exec(context, input);
            List<Value> inlist = new List<Value>();
            inlist_().ForEach(x => { inlist.Add(x.Exec(context, input)); });
            return inlist.Exists(v.Equals);
        }

        public override string ToString() => $"{expr_()} in ({string.Join(",", inlist_())})";
    }

}
