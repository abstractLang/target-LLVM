using LLVMSharp.Interop;
using Llvm = LLVMSharp.Interop.LLVM;
namespace Tq.Module.LLVM.Compiler;

internal unsafe partial class LlvmCompiler
{
    
    private LLVMTypeRef LlvmVoid => _llvmCtx.VoidType;
    private LLVMTypeRef LlvmBool => _llvmCtx.Int1Type;
    private LLVMTypeRef LlvmInt8 => _llvmCtx.Int8Type;
    private LLVMTypeRef LlvmInt16 => _llvmCtx.Int16Type;
    private LLVMTypeRef LlvmInt32 => _llvmCtx.Int32Type;
    private LLVMTypeRef LlvmInt64 => _llvmCtx.Int64Type;
    private LLVMTypeRef LlvmPtr => Llvm.PointerTypeInContext(_llvmCtx, 0);
    private LLVMTypeRef LlvmInt(uint bitsize) => Llvm.IntTypeInContext(_llvmCtx, bitsize);
    private LLVMTypeRef LlvmArray(LLVMTypeRef ety, uint count) => Llvm.ArrayType2(ety, count);

    private LLVMValueRef LlvmUndef(LLVMTypeRef ety) => Llvm.GetUndef(ety);
    
    private LLVMTypeRef LlvmFunctionType(LLVMTypeRef ReturnType, LLVMTypeRef[] ParamTypes)
    {
        fixed (LLVMTypeRef* pParamTypes = ParamTypes.AsSpan())
        {
            return Llvm.FunctionType(ReturnType, (LLVMOpaqueType**)pParamTypes, (uint)ParamTypes.Length, 0);
        }
    }
    private LLVMTypeRef LlvmStructType(LLVMTypeRef[] fieldTypes, bool packed)
    {
        return _llvmCtx.GetStructType(fieldTypes, packed);
    }

    private LLVMValueRef LlvmAddFunction(string name, LLVMTypeRef funcType)
    {
        using var marshaledString = new MarshaledString(name);
        return Llvm.AddFunction(_llvmModule, marshaledString, funcType);
    }
    
    private LLVMBasicBlockRef LlvmAppendBasicBlock(LLVMValueRef function, string name)
    {
        return Llvm.AppendBasicBlockInContext(_llvmCtx, function, new MarshaledString(name));
    }

    private void LlvmSetGlobalConst(LLVMValueRef g, bool isconst) => Llvm.SetGlobalConstant(g, isconst ? 0 : 1);
    
}