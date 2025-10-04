using System.Diagnostics;
using System.Numerics;
using System.Text;
using Abstract.Realizer.Builder;
using Abstract.Realizer.Builder.Language.Omega;
using Abstract.Realizer.Builder.ProgramMembers;
using Abstract.Realizer.Builder.References;
using Abstract.Realizer.Core.Configuration.LangOutput;
using LLVMSharp.Interop;

namespace Abstract.Module.LLVM.Compiler;

internal class LlvmCompiler
{
    private ILanguageOutputConfiguration configuration;
    
    private Dictionary<BaseFunctionBuilder, (LLVMTypeRef ftype, LLVMValueRef fobj)> functions = [];
    private Dictionary<StructureBuilder, LLVMTypeRef> structures = [];
    
    private LLVMContextRef llvmContext;
    private LLVMModuleRef llvmModule;
    private LLVMBuilderRef llvmBuilder;
    
    
    internal LLVMModuleRef Compile(ProgramBuilder program, ILanguageOutputConfiguration config) 
    {
        functions.Clear();
        llvmContext = LLVMContextRef.Create();
        
        configuration = config;
        var rootModule = program.GetRoot()!;
        
        llvmModule = llvmContext.CreateModuleWithName(rootModule.Symbol);
        llvmBuilder = llvmContext.CreateBuilder();
        
        DeclareModuleMembers(rootModule);
        
        CompileFunctions();
        CompileStructs();
        
        var ll = llvmModule.PrintToString();
        File.WriteAllText(".abs-cache/debug/llvmout.ll", ll);

        return llvmModule;
    }


    private void DeclareModuleMembers(ModuleBuilder baseModule)
    {
        // Abstract should have unnested all namespaces!
        foreach (var i in baseModule.Structures) UnwrapStructureHeader(i);
        foreach (var i in baseModule.Functions) UnwrapFunctionHeader(i);
    }

    
    private void UnwrapStructureHeader(StructureBuilder struc)
    {
        var st = llvmContext.CreateNamedStruct(struc.Symbol);
        structures.Add(struc, st);
    }
    private void UnwrapFunctionHeader(BaseFunctionBuilder baseFunc)
    {
        LLVMTypeRef[] argumentTypes = baseFunc.Parameters.Select(e => ConvType(e.type)).ToArray();
        LLVMTypeRef functype = LLVMTypeRef.CreateFunction(ConvType(baseFunc.ReturnType), argumentTypes, false);
        
        LLVMValueRef fun = llvmModule.AddFunction(baseFunc.Symbol, functype);
        
        switch (baseFunc)
        {
            case FunctionBuilder @func:
            {
                fun.Linkage = LLVMLinkage.LLVMDLLExportLinkage;
                fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
                
                fun.AddTargetDependentFunctionAttr("wasm-export-name", baseFunc.Symbol);
            } break;

            case ImportedFunctionBuilder importedFunc:
            {
                fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
                fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
                
                fun.AddTargetDependentFunctionAttr("wasm-import-module", importedFunc.ImportDomain ?? "env");
                fun.AddTargetDependentFunctionAttr("wasm-import-name", importedFunc.ImportSymbol!);
            } break;
            
            default: throw new UnreachableException();
        }
        
        functions.Add(baseFunc, (functype, fun));
    }


    private void CompileStructs()
    {
        foreach (var (baseStruct, llvmStruct) in structures)
        {
            CompileStruct(baseStruct, llvmStruct);
        }
    }
    private void CompileStruct(StructureBuilder baseStruct, LLVMTypeRef llvmStruct)
    {
        List<LLVMTypeRef> fields = [];
        foreach (var field in baseStruct.Fields)
            fields.Add(ConvType(field.Type!));
        
        llvmStruct.StructSetBody(fields.ToArray(), false);
    }
    
    private void CompileFunctions()
    {
        foreach (var (baseFunction, (llvmFuncType, llvmFunction)) in functions)
        {
            if (baseFunction is not FunctionBuilder @fb) continue;
            CompileFunction(fb, llvmFunction);
        }
    }
    private void CompileFunction(FunctionBuilder baseFunc, LLVMValueRef llvmFunction)
    {
        var entry = llvmFunction.AppendBasicBlock("entry");
        llvmBuilder.PositionAtEnd(entry);
        
        var args = new LLVMValueRef[llvmFunction.ParamsCount];
        foreach (var (i, (_, type)) in baseFunc.Parameters.Index())
        {
            var paramValue = llvmFunction.GetParam((uint)i);
            if (type is NodeTypeReference @nt && nt.TypeReference is StructureBuilder)
            {
                var local = llvmBuilder.BuildAlloca(paramValue.TypeOf);
                llvmBuilder.BuildStore(paramValue, local);
                paramValue = local;
            }

            args[i] = paramValue;
        }
        
        List<(TypeReference, LLVMValueRef)> locals = [];
        var body = new Queue<IOmegaInstruction>((baseFunc.BytecodeBuilder as OmegaBytecodeBuilder 
                                                 ?? throw new Exception("Expected OmegaBytecodeBuilder")).InstructionsList);

        var ctx = new CompileFunctionCtx
        {
            args = args,
            locals = locals,
            body = body,
        };
        
        while (body.Count > 0) CompileFunctionInstruction(ctx);
    }

    private void CompileFunctionInstruction(CompileFunctionCtx ctx)
    {
        var a = ctx.body.Peek();
        switch (a)
        {
            case MacroDefineLocal @deflocal:
                ctx.body.Dequeue();
                ctx.locals.Add((deflocal.Type, llvmBuilder.BuildAlloca(ConvType(deflocal.Type))));
                break;

            case InstStLocal @stlocal:
            {
                ctx.body.Dequeue();
                var val = CompileFunctionValueNullable(ctx);
                if (!val.HasValue) return;
                llvmBuilder.BuildStore(val.Value, ctx.locals[stlocal.index].ptr);
            } break;
            
            default:
                CompileFunctionValue(ctx);
                break;
        }
    }

    private LLVMValueRef CompileFunctionValue(CompileFunctionCtx ctx)
    {
        var v = CompileFunctionValueNullable(ctx);
        if (v.HasValue) return v.Value;
        throw new UnreachableException();
    }
    private LLVMValueRef? CompileFunctionValueNullable(CompileFunctionCtx ctx)
    {
        var a = ctx.body.Dequeue();
        LLVMValueRef val;
        TypeReference? holding = null;
        
        switch (a)
        {
            case InstLdConstIptr: throw new Exception("iptrs should've already been reassigned to a valid integer size");
            case InstLdConstI @ldconstix:
            {
                val = ldconstix.Len switch
                {
                    1  => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, unchecked((ulong)(Int128)ldconstix.Value), true),
                    8  => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, unchecked((ulong)(Int128)ldconstix.Value), true),
                    16 => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, unchecked((ulong)(Int128)ldconstix.Value), true),
                    32 => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, unchecked((ulong)(Int128)ldconstix.Value), true),
                    64 => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, unchecked((ulong)(Int128)ldconstix.Value), true),
                    _  => LLVMValueRef.CreateConstIntOfArbitraryPrecision(
                        LLVMTypeRef.CreateInt(ldconstix.Len), BigIntegerToULongs(ldconstix.Value, ldconstix.Len)),
                };
                break;
            }

            case InstLdStringUtf8 @str:
            {
                var elementType = LLVMTypeRef.Int8;
                var sliceType = CreateSlice(elementType);
                
                var bytes = Encoding.UTF8.GetBytes(str.Value);
                var data = LLVMValueRef.CreateConstArray(LLVMTypeRef.Int8, bytes
                    .Select(e => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, e)).ToArray());
                
                var globalBytes = llvmModule.AddGlobal(elementType, "string");
                globalBytes.Initializer = data;


                var gep = llvmBuilder.BuildInBoundsGEP2(
                    LLVMTypeRef.CreatePointer(elementType, 0), globalBytes,
                    [LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)]);
                
                return LLVMValueRef.CreateConstNamedStruct(sliceType, [gep,
                    LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)bytes.Length)]);
            }
            
            case InstLdLocal @ldlocal:
                if (ldlocal.Local < 0) val = ctx.args[(-ldlocal.Local) - 1];
                else
                {
                    val = ctx.locals[ldlocal.Local].ptr;
                    holding = ctx.locals[ldlocal.Local].type;
                }
                break;
            
            case InstLdNewObject: return null;

            case InstRet @r:
                return r.value
                    ? llvmBuilder.BuildRet(CompileFunctionValue(ctx))
                    : llvmBuilder.BuildRetVoid();
            
            case FlagTypeInt @tint:
            {
                var ty = new IntegerTypeReference(tint.Signed, tint.Size);
                return CompileFunctionValueTyped(ctx, ty);
            }

            case InstCall @call:
            {
                List<LLVMValueRef> argsList = [];
                
                for (var i = 0; i < call.function.Parameters.Count; i++) 
                    argsList.Add(CompileFunctionValue(ctx));

                var funck = BuilderToValueRef(call.function);
                
                val = llvmBuilder.BuildCall2(
                    funck.ftype,
                    funck.fun,
                    argsList.ToArray());
            } break;
            
            default: throw new UnreachableException();
        }
        
        while (ctx.body.Count > 0)
        {
            var curr = ctx.body.Peek();
            if (curr is InstLdField)
            {
                val = GetFieldPtr(val, curr, out holding);
                ctx.body.Dequeue();
            }
            else if (curr is InstStField)
            {
                var ptr = GetFieldPtr(val, curr, out holding);
                ctx.body.Dequeue();
                var tostore = CompileFunctionValue(ctx);
                val = llvmBuilder.BuildStore(tostore, ptr);
                holding = null;
                break;
            }
            else break;
        }

        return holding == null
            ? val
            : llvmBuilder.BuildLoad2(ConvType(holding), val);
    }

    private LLVMValueRef CompileFunctionValueTyped(CompileFunctionCtx ctx, TypeReference ty)
    {
        var a = ctx.body.Dequeue();
        switch (a)
        {
            case InstAdd: return llvmBuilder.BuildAdd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstMul: return llvmBuilder.BuildMul(CompileFunctionValue(ctx), CompileFunctionValue(ctx));

            case InstAnd: return llvmBuilder.BuildAnd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstOr: return llvmBuilder.BuildOr(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstXor: return llvmBuilder.BuildXor(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstConv: return llvmBuilder.BuildIntCast(CompileFunctionValue(ctx), ConvType(ty));
            case InstExtend:
                return (((IntegerTypeReference)ty).Signed)
                    ? llvmBuilder.BuildSExt(CompileFunctionValue(ctx), ConvType(ty))
                    : llvmBuilder.BuildZExt(CompileFunctionValue(ctx), ConvType(ty));
            case InstTrunc: return llvmBuilder.BuildTrunc(CompileFunctionValue(ctx), ConvType(ty));
            case InstSigcast: return CompileFunctionValue(ctx); // LLVM handles signess in context
            
            default: throw new UnreachableException();
        }
    }
    
    private LLVMValueRef GetFieldPtr(
        LLVMValueRef from,
        IOmegaInstruction access,
        out TypeReference ptrType)
    {
        switch (access)
        {
            case InstStField @stfield:
            {
                var field = stfield.StaticField;
                var struc = (StructureBuilder)field.Parent!;
                var fidx = (uint)struc.Fields.IndexOf(field);

                ptrType = field.Type!;
                return llvmBuilder.BuildStructGEP2(BuilderToTypeRef(struc), from, fidx);
            }
            
            case InstLdField @ldfield:
            {
                var field = ldfield.StaticField;
                var struc = (StructureBuilder)field.Parent!;
                var fidx = (uint)struc.Fields.IndexOf(field);
                
                ptrType = field.Type!;
                return llvmBuilder.BuildStructGEP2(BuilderToTypeRef(struc), from, fidx);
            }

            default: throw new UnreachableException();
        }
    }


    private LLVMTypeRef ConvType(TypeReference? typeref)
    {
        if (typeref == null) return LLVMTypeRef.Void;
        return typeref switch
        {
            IntegerTypeReference @intt => intt.Bits! switch
            {
                1 => LLVMTypeRef.Int1,
                8 => LLVMTypeRef.Int8,
                16 => LLVMTypeRef.Int16,
                32 => LLVMTypeRef.Int32,
                64 => LLVMTypeRef.Int64,
                _ => LLVMTypeRef.CreateInt((uint)intt.Bits!),
            },
            
            
            NodeTypeReference @nodet => nodet.TypeReference switch
            {
              StructureBuilder @stb => BuilderToTypeRef(stb),
            },
            
            SliceTypeReference @slice => CreateSlice(ConvType(slice.Subtype)),
            
            _ => throw new UnreachableException()
        };
        
    }

    
    private (LLVMTypeRef ftype, LLVMValueRef fun) BuilderToValueRef(BaseFunctionBuilder builder) => functions[builder];
    private LLVMTypeRef BuilderToTypeRef(StructureBuilder builder) => structures[builder];

    private LLVMTypeRef CreateSlice(LLVMTypeRef elementType) => LLVMTypeRef.CreateStruct([
        LLVMTypeRef.CreatePointer(elementType, 0), GetNativeInt()], false);

    private LLVMTypeRef GetNativeInt() => configuration.NativeIntegerSize switch
    {
        1 => LLVMTypeRef.Int1,
        8 => LLVMTypeRef.Int8,
        16 => LLVMTypeRef.Int16,
        32 => LLVMTypeRef.Int32,
        64 => LLVMTypeRef.Int64,
        _ => LLVMTypeRef.CreateInt(configuration.NativeIntegerSize),
    }; 
        
    private ulong[] BigIntegerToULongs(BigInteger value, int numBits)
    {
        if (value.Sign < 0)
            throw new ArgumentException("somente positivos suportados");

        int numWords = (numBits + 63) / 64;
        ulong[] words = new ulong[numWords];

        BigInteger remaining = value;
        for (int i = 0; i < numWords; i++)
        {
            words[i] = (ulong)(remaining & 0xFFFFFFFFFFFFFFFF);
            remaining >>= 64;
        }

        return words;
    }

    private struct CompileFunctionCtx
    {
        public LLVMValueRef[] args;
        public List<(TypeReference type, LLVMValueRef ptr)> locals;
        public Queue<IOmegaInstruction> body;
    }
}