using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Abstract.Realizer.Builder;
using Abstract.Realizer.Builder.Language.Omega;
using Abstract.Realizer.Builder.ProgramMembers;
using Abstract.Realizer.Builder.References;
using LLVMSharp.Interop;

namespace Abstract.Module.LLVM.Compiler;

internal static class LlvmCompiler
{
    private static Dictionary<BaseFunctionBuilder, (LLVMTypeRef ftype, LLVMValueRef fobj)> functions = [];
    private static Dictionary<StructureBuilder, LLVMTypeRef> structures = [];
    private static LLVMContextRef context;
    
    
    internal static LLVMModuleRef Compile(ProgramBuilder program) 
    {
        functions.Clear();
        context = LLVMContextRef.Create();
        
        var rootModule = program.GetRoot()!;
        
        var llvmModule = context.CreateModuleWithName(rootModule.Symbol);
        var llvmBuilder = context.CreateBuilder();
        
        DeclareModuleMembers(rootModule, llvmModule);
        
        CompileFunctions(llvmBuilder, llvmModule);
        CompileStructs(llvmBuilder, llvmModule);
        
        var ll = llvmModule.PrintToString();
        File.WriteAllText(".abs-cache/debug/llvmout.ll", ll);

        return llvmModule;
    }


    private static void DeclareModuleMembers(ModuleBuilder baseModule, LLVMModuleRef llvmModule)
    {
        // Abstract should have unnested all namespaces!
        foreach (var i in baseModule.Structures) UnwrapStructureHeader(i, llvmModule);
        foreach (var i in baseModule.Functions) UnwrapFunctionHeader(i, llvmModule);
    }

    
    private static void UnwrapStructureHeader(StructureBuilder struc, LLVMModuleRef llvmModule)
    {
        var st = context.CreateNamedStruct(struc.Symbol);
        structures.Add(struc, st);
    }
    private static void UnwrapFunctionHeader(BaseFunctionBuilder baseFunc, LLVMModuleRef llvmModule)
    {
        LLVMTypeRef[] argumentTypes = baseFunc.Parameters.Select(e => ConvType(e.type)).ToArray();
        LLVMTypeRef functype = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, argumentTypes, false);
        
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


    private static void CompileStructs(LLVMBuilderRef llvmBuilder, LLVMModuleRef llvmModule)
    {
        foreach (var (baseStruct, llvmStruct) in structures)
        {
            CompileStruct(baseStruct, llvmStruct, llvmBuilder);
        }
    }
    private static void CompileStruct(StructureBuilder baseStruct, LLVMTypeRef llvmStruct, LLVMBuilderRef llvmBuilder)
    {
        List<LLVMTypeRef> fields = [];
        foreach (var field in baseStruct.Fields)
            fields.Add(ConvType(field.Type!));
        
        llvmStruct.StructSetBody(fields.ToArray(), false);
    }
    
    private static void CompileFunctions(LLVMBuilderRef llvmBuilder, LLVMModuleRef llvmModule)
    {
        foreach (var (baseFunction, (llvmFuncType, llvmFunction)) in functions)
        {
            if (baseFunction is not FunctionBuilder @fb) continue;
            
            var entry = llvmFunction.AppendBasicBlock("entry");
            llvmBuilder.PositionAtEnd(entry);
            CompileFunction(fb, llvmFunction, llvmBuilder);
        }
    }
    private static void CompileFunction(FunctionBuilder baseFunc, LLVMValueRef llvmFunction, LLVMBuilderRef llvmBuilder)
    {
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
            builder = llvmBuilder
        };
        
        while (body.Count > 0) CompileFunctionInstruction(ctx);
        llvmBuilder.BuildRetVoid();
    }

    private static void CompileFunctionInstruction(CompileFunctionCtx ctx)
    {
        var a = ctx.body.Peek();
        switch (a)
        {
            case MacroDefineLocal @deflocal:
                ctx.body.Dequeue();
                ctx.locals.Add((deflocal.Type, ctx.builder.BuildAlloca(ConvType(deflocal.Type))));
                break;

            case InstStLocal @stlocal:
            {
                ctx.body.Dequeue();
                var val = CompileFunctionValueNullable(ctx);
                if (!val.HasValue) return;
                ctx.builder.BuildStore(val.Value, ctx.locals[stlocal.index].ptr);
            } break;
            
            case InstCall @call:
            {
                ctx.body.Dequeue();
                
                List<LLVMValueRef> argsList = [];
                
                for (var i = 0; i < call.function.Parameters.Count; i++) 
                    argsList.Add(CompileFunctionValue(ctx));

                var funck = BuilderToValueRef(call.function);
                
                ctx.builder.BuildCall2(
                    funck.ftype,
                    funck.fun,
                    argsList.ToArray());
            } break;
            
            default:
                CompileFunctionValue(ctx);
                break;
        }
    }

    private static LLVMValueRef CompileFunctionValue(CompileFunctionCtx ctx)
    {
        var v = CompileFunctionValueNullable(ctx);
        if (v.HasValue) return v.Value;
        throw new UnreachableException();
    }
    private static LLVMValueRef? CompileFunctionValueNullable(CompileFunctionCtx ctx)
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
                    ? ctx.builder.BuildRetVoid()
                    : ctx.builder.BuildRet(CompileFunctionValue(ctx));
            
            case FlagTypeInt @tint:
            {
                var ty = new IntegerTypeReference(tint.Signed, tint.Size);
                return CompileFunctionValueTyped(ctx, ty);
            }

            default: throw new UnreachableException();
        }
        
        while (ctx.body.Count > 0)
        {
            var curr = ctx.body.Peek();
            if (curr is InstLdField)
            {
                val = GetFieldPtr(val, curr, ctx.builder, out holding);
                ctx.body.Dequeue();
            }
            else if (curr is InstStField)
            {
                var ptr = GetFieldPtr(val, curr, ctx.builder, out holding);
                ctx.body.Dequeue();
                var tostore = CompileFunctionValue(ctx);
                val = ctx.builder.BuildStore(tostore, ptr);
                holding = null;
                break;
            }
            else break;
        }

        return holding == null
            ? val
            : ctx.builder.BuildLoad2(ConvType(holding), val);
    }

    private static LLVMValueRef CompileFunctionValueTyped(CompileFunctionCtx ctx, TypeReference ty)
    {
        var a = ctx.body.Dequeue();
        switch (a)
        {
            case InstAdd: return ctx.builder.BuildAdd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstMul: return ctx.builder.BuildMul(CompileFunctionValue(ctx), CompileFunctionValue(ctx));

            case InstAnd: return ctx.builder.BuildAnd(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstOr: return ctx.builder.BuildOr(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            case InstXor: return ctx.builder.BuildXor(CompileFunctionValue(ctx), CompileFunctionValue(ctx));
            
            case InstConv: return ctx.builder.BuildIntCast(CompileFunctionValue(ctx), ConvType(ty));
            case InstExtend:
                return (((IntegerTypeReference)ty).Signed)
                    ? ctx.builder.BuildSExt(CompileFunctionValue(ctx), ConvType(ty))
                    : ctx.builder.BuildZExt(CompileFunctionValue(ctx), ConvType(ty));
            case InstTrunc: return ctx.builder.BuildTrunc(CompileFunctionValue(ctx), ConvType(ty));
            case InstSigcast: return CompileFunctionValue(ctx); // LLVM handles signess in context
            
            default: throw new UnreachableException();
        }
    }
    
    private static LLVMValueRef GetFieldPtr(
        LLVMValueRef from,
        IOmegaInstruction access,
        LLVMBuilderRef builder,
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
                return builder.BuildStructGEP2(BuilderToTypeRef(struc), from, fidx);
            }
            
            case InstLdField @ldfield:
            {
                var field = ldfield.StaticField;
                var struc = (StructureBuilder)field.Parent!;
                var fidx = (uint)struc.Fields.IndexOf(field);
                
                ptrType = field.Type!;
                return builder.BuildStructGEP2(BuilderToTypeRef(struc), from, fidx);
            }

            default: throw new UnreachableException();
        }
    }


    private static LLVMTypeRef ConvType(TypeReference typeref)
    {
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
            
            _ => throw new UnreachableException()
        };
        
    }

    
    private static (LLVMTypeRef ftype, LLVMValueRef fun) BuilderToValueRef(BaseFunctionBuilder builder) => functions[builder];
    private static LLVMTypeRef BuilderToTypeRef(StructureBuilder builder) => structures[builder];
    
    private static ulong[] BigIntegerToULongs(BigInteger value, int numBits)
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
        public LLVMBuilderRef builder;
    }
}