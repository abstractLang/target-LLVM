using LLVMSharp.Interop;
using Llvm = LLVMSharp.Interop.LLVM;
namespace Abstract.Module.LLVM.Compiler;

internal unsafe partial class LlvmCompiler
{
    
    private LLVMTypeRef LlvmVoid => ctx.VoidType;
    private LLVMTypeRef LlvmBool => ctx.Int1Type;
    private LLVMTypeRef LlvmInt8 => ctx.Int8Type;
    private LLVMTypeRef LlvmInt16 => ctx.Int16Type;
    private LLVMTypeRef LlvmInt32 => ctx.Int32Type;
    private LLVMTypeRef LlvmInt64 => ctx.Int64Type;
    private LLVMTypeRef LlvmPtr(LLVMTypeRef basety) => Llvm.PointerType(basety, 0);
    private LLVMTypeRef LlvmOpaquePtr => Llvm.PointerTypeInContext(ctx, 0);
    private LLVMTypeRef LlvmInt(uint bitsize) => Llvm.IntTypeInContext(ctx, bitsize);
    private LLVMTypeRef LlvmArray(LLVMTypeRef ety, uint count) => Llvm.ArrayType(ety, count);

    private LLVMTypeRef LlvmFunctionType(LLVMTypeRef ReturnType, LLVMTypeRef[] ParamTypes)
    {
        fixed (LLVMTypeRef* pParamTypes = ParamTypes.AsSpan())
        {
            return Llvm.FunctionType(ReturnType, (LLVMOpaqueType**)pParamTypes, (uint)ParamTypes.Length, 0);
        }
    }

    private LLVMBasicBlockRef LLVMAppendBasicBlock(LLVMValueRef function, string name)
    {
        return Llvm.AppendBasicBlockInContext(ctx, function, new MarshaledString(name));
    }

}