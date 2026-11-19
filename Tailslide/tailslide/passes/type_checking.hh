#pragma once

#include "../lslmini.hh"
#include "../visitor.hh"

namespace Tailslide {
class TypeCheckVisitor: public DepthFirstASTVisitor {
  protected:
    virtual bool visit(LSLASTNode *node);
    virtual bool visit(LSLGlobalVariable *glob_var);
    virtual bool visit(LSLDeclaration *decl_stmt);
    virtual bool visit(LSLStateStatement *state_stmt);
    virtual bool visit(LSLReturnStatement *ret_stmt);
    virtual bool visit(LSLIfStatement *if_stmt);
    virtual bool visit(LSLForStatement *for_stmt);
    virtual bool visit(LSLDoStatement *do_stmt);
    virtual bool visit(LSLWhileStatement *while_stmt);
    virtual bool visit(LSLExpression *expr);
    virtual bool visit(LSLEventHandler *handler);
    virtual bool visit(LSLFunctionExpression *func_expr);
    virtual bool visit(LSLLValueExpression *lvalue);
    virtual bool visit(LSLTypecastExpression *cast_expr);
    virtual bool visit(LSLVectorExpression *vec_expr);
    virtual bool visit(LSLQuaternionExpression *quat_expr);
    virtual bool visit(LSLListConstant *list_const);
    virtual bool visit(LSLListExpression *list_expr);
    virtual bool visit(LSLPrintExpression *print_expr);
    
    void handleDeclaration(LSLASTNode *decl_node);
    void validateCheckExpr(LSLExpression *check_expr);
};

// A program embedding Tailslide may give a builtin function of its own several signatures: it defines one in the
// builtin symbol table and keeps the others itself. A resolver it sets is called for each call to a builtin function
// once the arguments have their types. It returns the signature the call takes (`sym` when there is nothing to
// choose), and the call's symbol and type follow it; or null after reporting an error on the call, for example
// E_AMBIGUOUS_OVERLOAD. Set it before checking scripts, not during a check. No resolver: no change. (Added by Legion Builds.)
typedef LSLSymbol *(*BuiltinCallResolver)(LSLSymbol *sym, LSLFunctionExpression *call);
void setBuiltinCallResolver(BuiltinCallResolver resolver);
}
