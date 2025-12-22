using System.Diagnostics;
using System.Numerics;
using System.Text;
using LLVMSharp.Interop;
using Tq.Module.LLVM.Targets;
using Tq.Realizeer.Core.Program;
using Tq.Realizeer.Core.Program.Builder;
using Tq.Realizeer.Core.Program.Member;
using Tq.Realizer.Core.Builder.Execution.Omega;
using Tq.Realizer.Core.Builder.References;
using Tq.Realizer.Core.Configuration.LangOutput;
using Tq.Realizer.Core.Intermediate.Values;
using static Tq.Realizer.Core.Builder.Language.Omega.OmegaInstructions;

namespace Tq.Module.LLVM.Compiler;

internal partial class LlvmCompiler
{
    private IOutputConfiguration _configuration;
    private TargetsList _target;
    private LLVMContextRef _llvmCtx;
    
    private Dictionary<RealizerFunction, (LLVMTypeRef ftype, LLVMValueRef fobj)> _functions = [];
    private Dictionary<dynamic, (LLVMTypeRef ftype, LLVMValueRef fobj)> _staticFields = [];
    private Dictionary<RealizerStructure, (LLVMTypeRef type, LLVMValueRef typetbl)> _structures = [];
    private Dictionary<string, RealizerFunction> _exportMap = [];
    
    private Dictionary<string, LLVMValueRef> _intrinsincs = [];

    private LLVMModuleRef _llvmModule;
    private LLVMBuilderRef _llvmBuilder;

    private uint metanameCount = 0;
    private Dictionary<int, LLVMValueRef> _staticMap = [];


    public LlvmCompiler(LLVMContextRef llvmCtx, TargetsList llvm)
    {
        _llvmCtx = llvmCtx;
        _target = llvm;
    }

    internal LLVMModuleRef Compile(RealizerProgram program, IOutputConfiguration config) 
    {
        _functions.Clear();
        _structures.Clear();
        _intrinsincs.Clear();
        _configuration = config;
        
        _llvmModule = _llvmCtx.CreateModuleWithName(program.Name);
        _llvmBuilder = _llvmCtx.CreateBuilder();

        InitializeIntrinsics();        
        foreach (var m in program.Modules) DeclareModuleMembers(m);

        // Order matters here
        CompileFunctions();
        CompileStructs();
        
        
        var ll = _llvmModule.PrintToString();
        File.WriteAllText($".abs-cache/debug/{program.Name}.llvmout.ll", ll);
        
        if (!_llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var msg))
            File.WriteAllText($".abs-cache/debug/{program.Name}.llvmdump.txt", msg);
        
        return _llvmModule;
    }

    private void InitializeIntrinsics()
    {
        LLVMTypeRef type;
        LLVMValueRef func;


        type = LlvmFunctionType(LlvmStructType([LlvmInt8, LlvmBool], false), [LlvmInt8, LlvmInt8]);
        func = LlvmAddFunction("llvm.sadd.with.overflow.i8", type);
        _intrinsincs.Add("sadd.w.ovf.i8", func);
        func = LlvmAddFunction("llvm.uadd.with.overflow.i8", type);
        _intrinsincs.Add("uadd.w.ovf.i8", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt16, LlvmBool], false), [LlvmInt16, LlvmInt16]);
        func = LlvmAddFunction("llvm.sadd.with.overflow.i16", type);
        _intrinsincs.Add("sadd.w.ovf.i16", func);
        func = LlvmAddFunction("llvm.uadd.with.overflow.i16", type);
        _intrinsincs.Add("uadd.w.ovf.i16", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt32, LlvmBool], false), [LlvmInt32, LlvmInt32]);
        func = LlvmAddFunction("llvm.sadd.with.overflow.i32", type);
        _intrinsincs.Add("sadd.w.ovf.i32", func);
        func = LlvmAddFunction("llvm.uadd.with.overflow.i32", type);
        _intrinsincs.Add("uadd.w.ovf.i32", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt64, LlvmBool], false), [LlvmInt64, LlvmInt64]);
        func = LlvmAddFunction("llvm.sadd.with.overflow.i64", type);
        _intrinsincs.Add("sadd.w.ovf.i64", func);
        func = LlvmAddFunction("llvm.uadd.with.overflow.i64", type);
        _intrinsincs.Add("uadd.w.ovf.i64", func);
        
        switch (_target)
        {
            case TargetsList.Wasm:
            {
                type = LlvmFunctionType(LlvmInt32, []);
                func = LlvmAddFunction("llvm.wasm.memory.size", type);
                _intrinsincs.Add("wasm.memory.size", func);
                
                type = LlvmFunctionType(LlvmInt32, [LlvmInt32]);
                func = LlvmAddFunction("llvm.wasm.memory.grow", type);
                _intrinsincs.Add("wasm.memory.grow", func);

            } break;
            default: throw new ArgumentOutOfRangeException();
        }
        
    }

    private void DeclareModuleMembers(RealizerModule baseModule)
    {
        // Abstract should have unnested all namespaces!
        foreach (var i in baseModule.GetMembers())
        {
            switch (i)
            {
                case RealizerFunction @i2: UnwrapFunctionHeader(i2); break;
                case RealizerField @i2: UnwrapStaticFieldHeader(i2); break;
                case RealizerStructure @i2: UnwrapStructureHeader(i2); break;
            }
        }
    }

    private void UnwrapStaticFieldHeader(RealizerField field)
    {
        var t = ConvType(field.Type);
        var gb = _llvmModule.AddGlobal(t, field.Name);
        gb.Linkage = LLVMLinkage.LLVMInternalLinkage;
        
        if (field.Initializer != null) gb.Initializer = Const2Llvm(field.Initializer).v;
        
        _staticFields.Add(field, (t, gb));
    }
    private void UnwrapStructureHeader(RealizerStructure struc)
    {
        var st = _llvmCtx.CreateNamedStruct(struc.Name);
        
        var vtableType = LlvmStructType([
            LlvmPtr, // Parent table
            GetNativeInt(), // Type size
            GetNativeInt(), // Type alignment
            CreateSlice(LlvmInt8), // StructName
            GetNativeInt(), // Vtable length
            LlvmArray(LlvmPtr, 0) // Table
        ], false);
        var vtableGlobal = _llvmModule.AddGlobal(vtableType, struc.Name + ".vtable");
        
        _structures.Add(struc, (st, vtableGlobal));
        unsafe {LLVMSharp.Interop.LLVM.SetGlobalConstant(vtableGlobal, 1);}

        // if (struc.VTableSize.HasValue)
        // {
        //     var vtlist = new VirtualFunctionBuilder[struc.VTableSize.Value];
        //     foreach (var virt in struc.Functions.OfType<VirtualFunctionBuilder>())
        //         if (virt.CodeBlocks.Count == 0) vtlist[virt.Index] = virt;
        //     _vtables.Add(struc, vtlist);
        // }
    }

    private void UnwrapFunctionHeader(RealizerFunction baseFunc)
    {
        var argumentTypes = baseFunc.Parameters.Select(e => ConvType(e.Type)).ToArray();
        var functype = LlvmFunctionType(ConvType(baseFunc.ReturnType), argumentTypes);

        var fun = LlvmAddFunction(baseFunc.Name, functype);

        if (baseFunc.ExportSymbol != null)
        {
            fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
            fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
            fun.AddTargetDependentFunctionAttr("wasm-export-name", baseFunc.ExportSymbol);
        }

        if (baseFunc.ImportSymbol != null)
        {
            fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
            fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;

            fun.AddTargetDependentFunctionAttr("wasm-import-module", baseFunc.ImportDomain ?? "env");
            fun.AddTargetDependentFunctionAttr("wasm-import-name", baseFunc.ImportSymbol);
        }

        _functions.Add(baseFunc, (functype, fun));
            
    }


    private void CompileFunctions()
    {
        foreach (var (baseFunction, (llvmFuncType, llvmFunction)) in _functions)
            if (baseFunction is { ExecutionBlocksCount: > 0 } @fb) CompileFunction(fb, llvmFunction);
    }
    private void CompileFunction(RealizerFunction baseFunc, LLVMValueRef llvmFunction)
    {
        var baseargs = baseFunc.Parameters;
        var llvmargs = llvmFunction.GetParams();
        Dictionary<RealizerParameter, LLVMValueRef> finalargs = [];
        for (var i = 0; i < llvmargs.Length; i++) finalargs.Add(baseargs[i], llvmargs[i]);
        finalargs.TrimExcess();
        
        var codeCells = new (OmegaCodeCell baseCell, LLVMBasicBlockRef llvmBlock)[baseFunc.ExecutionBlocksCount];
        foreach (var (i, block) in baseFunc.ExecutionBlocks.Index())
        {
            if (block is not OmegaCodeCell @omega) throw new Exception("Expected OmegaBytecodeBuilder");
            
            var llvmblock = LlvmAppendBasicBlock(llvmFunction, block.Name);
            codeCells[i] = (omega, llvmblock);
        }

        if (codeCells.Length > 0)
        {
            _llvmBuilder.PositionAtEnd(codeCells[0].llvmBlock);
            foreach (var (i, p) in baseFunc.Parameters.Index())
            {
                var paramValue = llvmFunction.GetParam((uint)i);
                if (p.Type is NodeTypeReference { TypeReference: RealizerStructure @struc })
                {
                    var local = _llvmBuilder.BuildAlloca(paramValue.TypeOf);
                    var store = _llvmBuilder.BuildStore(paramValue, local);
                    var align = AlignOf(struc);
                    local.SetAlignment(align);
                    store.SetAlignment(align);
                
                    paramValue = local;
                }

                finalargs[p] = paramValue;
            }
        }

        var count = 0;
        foreach (var (cell, llvmblock) in codeCells)
        {
            var body = new Queue<IOmegaInstruction>(cell.Instructions);
            var ctx = new CompileCodeBlockCtx {
               RealizerFunction = baseFunc,
               LlvmFunction = llvmFunction,
               BlockMap = codeCells,
               Args = finalargs,
               Registers = [],
            };
            
            _llvmBuilder.PositionAtEnd(llvmblock);
            foreach (var i in body)
                CompileCodeBlockInstruction(_llvmBuilder, i, ctx);
        }
    }

    
    private void CompileStructs()
    {
        foreach (var (baseStruct, (llvmStruct, tt)) in _structures)
            CompileStruct(baseStruct, llvmStruct, tt);
    }
    private void CompileStruct(RealizerStructure baseStruct, LLVMTypeRef llvmStruct, LLVMValueRef typeTbl)
    {
        List<LLVMTypeRef> fields = [];
        
        fields.Add(LlvmPtr);
        if (baseStruct.Extends != null)
            fields.AddRange(baseStruct.Extends.GetMembers<RealizerField>().Select(field => ConvType(field.Type!)));
        fields.AddRange(baseStruct.GetMembers<RealizerField>().Select(field => ConvType(field.Type!)));
        
        llvmStruct.StructSetBody(fields.ToArray(), false);
        
        var parentTablePointer = baseStruct.Extends == null
            ? LLVMValueRef.CreateConstPointerNull(LlvmPtr)
            : _structures[baseStruct.Extends].typetbl;
        
        List<LLVMValueRef> values = [];

        var identifier = baseStruct.GlobalString;
        
        typeTbl.Initializer = LLVMValueRef.CreateConstStruct([
            parentTablePointer,
            LLVMValueRef.CreateConstInt(GetNativeInt(), baseStruct.Length),
            LLVMValueRef.CreateConstInt(GetNativeInt(), baseStruct.Alignment),
            LLVMValueRef.CreateConstStruct([
                StoreMetadataStructName(identifier),
                LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)identifier.Length),
                ], false),
            LLVMValueRef.CreateConstInt(GetNativeInt(), 0),
            LLVMValueRef.CreateConstArray(LlvmPtr, [])
        ], false);
    }

    
    
    private void CompileCodeBlockInstruction(
        LLVMBuilderRef builder,
        IOmegaInstruction instruction,
        CompileCodeBlockCtx ctx)
    {
        switch (instruction)
        {
            case Assignment @assignment:
                CompileExecCellValue_assignment(builder, assignment, ctx);
                break;

            case Ret @ret:
            {
                if (ret.Value == null) builder.BuildRetVoid();
                else builder.BuildRet(CompileExecCellValue(builder, ret.Value, ctx));
            } break;

            case Throw @throw: builder.BuildUnreachable(); break;
            
            case IOmegaExpression @v: CompileExecCellValue(builder, v, ctx); break;
            
            default: throw new UnreachableException();
        }
    }
    private LLVMValueRef CompileExecCellValue(
        LLVMBuilderRef builder,
        IOmegaExpression instExpression,
        CompileCodeBlockCtx ctx,
        AccessMode access = AccessMode.Value,
        LLVMValueRef? inPointer = null)
    {
        switch (instExpression)
        {
            case Add @add:
                return builder.BuildAdd(
                    CompileExecCellValue(builder, add.Left, ctx),
                    CompileExecCellValue(builder, add.Right, ctx));
            
            // Sub @sub => return builder.BuildSub(
            //        CompileExecCellValue(builder, sub.Left, ctx),
            //        CompileExecCellValue(builder, sub.Right, ctx));
            
            case Mul @mul:
                return builder.BuildMul(
                    CompileExecCellValue(builder, mul.Left, ctx),
                    CompileExecCellValue(builder, mul.Right, ctx));

            case Access @acc:
            {
                var baseptr = CompileExecCellValue(builder, acc.Left, ctx, AccessMode.Ptr);
                return CompileExecCellValue(builder, acc.Right, ctx, access, baseptr);
            }
            
            case Constant @const: return CompileExecCellValue_constant(builder, @const, ctx);
                
            
            case Call @call:
            {
                var fn = CompileExecCellValue_callable(builder, call.Callable, ctx);
                var args = call.Arguments.Select(e => CompileExecCellValue(builder, e, ctx)).ToArray();
                
                return builder.BuildCall2(fn.ftype, fn.fun, args);
            }
            
            case Member @m: return CompileExecCellValue_member(builder, m, ctx, access, inPointer);
            
            case Argument @a: return ctx.Args[a.Parameter];

            case Register @r:
                return ctx.Registers[r.Index];
            
            case Alloca @a:
            {
                var alloca = builder.BuildAlloca(ConvType(a.AllocaType));
                // FIXME
                // technically accessing it as value is UB,
                // see if it is a desired possible behavior
                return access == AccessMode.Ptr
                    ? alloca
                    : builder.BuildLoad2(ConvType(((ReferenceTypeReference)a.Type).Subtype), alloca);
            }

            case Cmp @c:
                return builder.BuildICmp(c.Op switch
                    {
                        ComparissonOperation.Equal => LLVMIntPredicate.LLVMIntEQ,
                        ComparissonOperation.NotEqual => LLVMIntPredicate.LLVMIntNE,
                        
                        ComparissonOperation.SignedLessThan => LLVMIntPredicate.LLVMIntSLT,
                        ComparissonOperation.SignedLessThanOrEqual => LLVMIntPredicate.LLVMIntSLE,
                        ComparissonOperation.SignedGreaterThan => LLVMIntPredicate.LLVMIntSGT,
                        ComparissonOperation.SignedGreaterThanOrEqual => LLVMIntPredicate.LLVMIntSGE,
                        
                        ComparissonOperation.UnsignedLessThan => LLVMIntPredicate.LLVMIntULT,
                        ComparissonOperation.UnsignedLessThanOrEqual => LLVMIntPredicate.LLVMIntULE,
                        ComparissonOperation.UnsignedGreaterThan => LLVMIntPredicate.LLVMIntUGT,
                        ComparissonOperation.UnsignedGreaterThanOrEqual => LLVMIntPredicate.LLVMIntUGE,
                        
                        _ => throw new ArgumentOutOfRangeException()
                    },
                    CompileExecCellValue(builder, c.Left, ctx),
                    CompileExecCellValue(builder, c.Right, ctx));

            case IntTypeCast @it:
                return builder.BuildIntCast(CompileExecCellValue(builder, it.Exp, ctx), ConvType(it.Type));
            
            case IntFromPtr @itp:
                return builder.BuildPtrToInt(CompileExecCellValue(builder, itp.Expression, ctx), ConvType(itp.Type));
            
            case PtrFromInt @pti:
                return builder.BuildIntToPtr(CompileExecCellValue(builder, pti.Expression, ctx), ConvType(pti.Type));
            
            case Ref @r:
                return CompileExecCellValue(builder, r.Expression, ctx, AccessMode.Ptr);

            case Val @v:
            {
                var ptr = CompileExecCellValue(builder, v.Expression, ctx, AccessMode.Ptr);
                var val = builder.BuildLoad2(ConvType(v.Type), ptr);
                val.SetAlignment(AlignOf(v.Type));
                return val;
            }

            case Typeof @t:
            {

                var ty = t.Type;
                while (true)
                {
                    switch (ty)
                    {
                        case NodeTypeReference { TypeReference: RealizerStructure @rs }:
                            return _structures[rs].typetbl;

                        case MetadataTypeReference @meta:
                            ty = meta.Typeof;
                            continue;
                        
                        case ReferenceTypeReference @refe:
                            ty = refe.Subtype;
                            continue;

                        default:
                            throw new UnreachableException();
                    }
                }
            }

            case LenOf @lenOf:
            {
                var val = CompileExecCellValue(builder, lenOf.Expression, ctx);
                return builder.BuildExtractValue(val, 1);
            }
            
            default: throw new UnreachableException();
        }
    }

    
    private LLVMValueRef CompileExecCellValue_member(
        LLVMBuilderRef builder,
        Member member,
        CompileCodeBlockCtx ctx,
        AccessMode access = AccessMode.Value,
        LLVMValueRef? inPointer = null)
    {
        switch (member.Node)
        {
            case RealizerField { Static: true } f:
                if (access == AccessMode.Value)
                {
                    var load = builder.BuildLoad2(ConvType(f.Type), _staticFields[f].fobj);
                    load.SetAlignment(AlignOf(f.Type));
                    return load;
                }
                return _staticFields[f].fobj;

            case RealizerField { Static: false } f:
            {
                var parentType = MemberToTypeRef((RealizerStructure)f.Parent!);
                var fieldPtr = builder.BuildStructGEP2(parentType, inPointer!.Value, f.Index+1);

                if (access == AccessMode.Value)
                {
                    var load = builder.BuildLoad2(ConvType(f.Type), fieldPtr);
                    load.SetAlignment(AlignOf(f.Type));
                    return load;
                }
                return fieldPtr;
            }

            default: throw new UnreachableException();
        }
    }

    private (LLVMTypeRef ftype, LLVMValueRef fun) CompileExecCellValue_callable(LLVMBuilderRef builder,
        IOmegaCallable callable, CompileCodeBlockCtx ctx, AccessMode access = AccessMode.Value)
    {
        return callable switch
        {
            Member { Node: RealizerFunction @f } => _functions[f],
            
            _ => throw new UnreachableException()
        };
    }

    private void CompileExecCellValue_assignment(LLVMBuilderRef builder, Assignment assignment,
        CompileCodeBlockCtx ctx)
    {
        switch (assignment.Left)
        {
            case Member:
            {
                var ptr = CompileExecCellValue(builder, assignment.Left, ctx, AccessMode.Ptr);
                var val = CompileExecCellValue(builder, assignment.Right, ctx);
                var store = builder.BuildStore(val, ptr);
                store.SetAlignment(AlignOf(assignment.Left.Type));
            } break;

            case Register @r:
            {
                var val = CompileExecCellValue(builder, assignment.Right, ctx,
                    r.Type is ReferenceTypeReference ? AccessMode.Ptr : AccessMode.Value);
                ctx.Registers[r.Index] = val;
            } break;
        }
    }
    
    private LLVMValueRef CompileExecCellValue_constant(LLVMBuilderRef builder, Constant cons, CompileCodeBlockCtx ctx)
    {
        return cons.Value switch
        {
            IntegerConstantValue @icv => LLVMValueRef.CreateConstInt(ConvType(icv.Type),
                unchecked((ulong)(Int128)icv.Value)),

            NullConstantValue { Type: ReferenceTypeReference } @ncv => LLVMValueRef.CreateConstIntToPtr(
                LLVMValueRef.CreateConstNull(LlvmPtr), LlvmPtr),

            NullConstantValue { Type: SliceTypeReference } @ncv => LLVMValueRef.CreateConstStruct([
                LLVMValueRef.CreateConstNull(LlvmPtr), LLVMValueRef.CreateConstInt(GetNativeInt(), 0)], false),
            
            SliceConstantValue @slice => StoreGlobalArray(ConvType(slice.ElementType), slice.Content, true),
                    
            _ => throw new UnreachableException(),
        };
    }

    
    private LLVMTypeRef ConvType(TypeReference? typeref)
    {
        if (typeref == null) return LlvmVoid;
        return typeref switch
        {
            IntegerTypeReference @intt => intt.Bits! switch
            {
                0 => GetNativeInt(),
                1 => LlvmBool,
                8 => LlvmInt8,
                16 => LlvmInt16,
                32 => LlvmInt32,
                64 => LlvmInt64,
                _ => LlvmInt(intt.Bits),
            },
            
            NoreturnTypeReference or
            AnytypeTypeReference => LlvmVoid,
            
            NodeTypeReference @nodet => nodet.TypeReference switch
            { 
                RealizerStructure @stb => MemberToTypeRef(stb),
                RealizerTypedef => GetNativeInt(),
                _ => throw new UnreachableException(),
            },
            
            SliceTypeReference @slice => CreateSlice(ConvType(slice.Subtype)),
            ReferenceTypeReference @refe => LlvmPtr,
            NullableTypeReference @refe => refe.Subtype is ReferenceTypeReference
                    ? LlvmPtr
                    : LlvmStructType([ConvType(refe.Subtype), LlvmBool], true),
            
            _ => throw new UnreachableException()
        };
        
    }

    private uint BitsToMemUnit(uint size) => (uint)Math.Max(1, size  / _configuration.MemoryUnit);

    private uint AlignOf(RealizerStructure struc) => BitsToMemUnit(BitAlignOf(struc));
    private uint AlignOf(TypeReference? type) => BitsToMemUnit(BitAlignOf(type));

    private uint BitAlignOf(RealizerStructure struc) => (uint)struc.Alignment.ToInt(_configuration.NativeIntegerSize);
    private uint BitAlignOf(TypeReference? type)
    {
        if (type == null)
            return 0;
        return type switch
        {
            NodeTypeReference { TypeReference: RealizerStructure @struct } => @struct.Alignment,
            NodeTypeReference { TypeReference: RealizerTypedef @typedef } => BitAlignOf(typedef.BackingType
                ?? new IntegerTypeReference(false, _configuration.NativeIntegerSize)),
            
            IntegerTypeReference @intt => intt.Bits,
            ReferenceTypeReference or SliceTypeReference => _configuration.NativeIntegerSize,
            AnytypeTypeReference => throw new Exception("Anytype should already had ben solved"),
            
            _ => throw new UnreachableException(),
        };
    }
    
    private (LLVMTypeRef ftype, LLVMValueRef fun) MemberToValueRef(RealizerFunction member) => _functions[member];
    private (LLVMTypeRef ftype, LLVMValueRef fld) MemberToValueRef(RealizerField member) => _staticFields[member];
    private LLVMTypeRef MemberToTypeRef(RealizerStructure member) => _structures[member].type;


    private unsafe LLVMValueRef StoreMetadataStructName(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        var spanptr = stackalloc LLVMValueRef[bytes.Length];
        var span = new Span<LLVMValueRef>(spanptr, bytes.Length);
        for (var i = 0; i < bytes.Length; i++) span[i] = LLVMValueRef.CreateConstInt(LlvmInt8, bytes[i]);
        
        var llvmArrType = LLVMTypeRef.CreateArray2(LlvmInt8, (ulong)bytes.Length);
        var g = _llvmModule.AddGlobal(llvmArrType, $"ro.meta.name.{metanameCount++:x}");
        LlvmSetGlobalConst(g, true);
        g.Initializer = LLVMValueRef.CreateConstArray(LlvmInt8, span);

        return g;
    }

    private unsafe LLVMValueRef StoreGlobalString(string data, LLVMTypeRef elmtype)
    {
        var strdata = Encoding.UTF8.GetBytes(data);
        var array = new RealizerConstantValue[strdata.Length];

        for (var i = 0; i < strdata.Length; i++)
            array[i] = new IntegerConstantValue(new IntegerTypeReference(false, 8), strdata[i]);

        return StoreGlobalArray(LlvmInt8, array, true);
    }
    private LLVMValueRef StoreGlobalArray(LLVMTypeRef elmtype, RealizerConstantValue[] data, bool isConstant) 
    {
        var h = data.Aggregate(0, HashCode.Combine);

        if (_staticMap.TryGetValue(h, out var dedup)) return dedup;
        
        var arrayType = LlvmArray(elmtype, (uint)data.Length);
        var sliceType = CreateSlice(elmtype);
                
        var global = _llvmModule.AddGlobal(arrayType, $"ro.static.buffer.{_staticMap.Count:0000}");
        LlvmSetGlobalConst(global, isConstant);
        var datallvm = new LLVMValueRef[data.Length];
        for (var i = 0; i < data.Length; i++) datallvm[i] = Const2Llvm(data[i]).v;

        global.Alignment = 1;
        global.Initializer = LLVMValueRef.CreateConstArray(elmtype, datallvm);
        
        var gep = LLVMValueRef.CreateConstInBoundsGEP2(
            LLVMTypeRef.CreatePointer(elmtype, 0), global,
            [LLVMValueRef.CreateConstInt(LlvmInt16, 0),
                LLVMValueRef.CreateConstInt(LlvmInt16, 0)]);
                
        var ptr = LLVMValueRef.CreateConstNamedStruct(sliceType, [gep,
            LLVMValueRef.CreateConstInt(GetNativeInt(), (ulong)data.Length)]);

        _staticMap.Add(h, ptr);
        return ptr;
    }
    private (LLVMValueRef v, LLVMTypeRef t) Const2Llvm(RealizerConstantValue val)
    {
        switch (val)
        {
            case NullConstantValue:
                return (LLVMValueRef.CreateConstNull(LlvmPtr), LlvmPtr);
            
            case IntegerConstantValue i:
            {
                var value = unchecked((ulong)(Int128)i.Value);
                
                return i.Type.Bits switch
                {
                    0 => (LLVMValueRef.CreateConstInt(GetNativeInt(), value, true), LlvmBool),
                    1  => (LLVMValueRef.CreateConstInt(LlvmBool, value, true), LlvmBool),
                    8  => (LLVMValueRef.CreateConstInt(LlvmInt8, value, true), LlvmInt8),
                    16 => (LLVMValueRef.CreateConstInt(LlvmInt16, value, true), LlvmInt16),
                    32 => (LLVMValueRef.CreateConstInt(LlvmInt32, value, true), LlvmInt32),
                    64 => (LLVMValueRef.CreateConstInt(LlvmInt64, value, true), LlvmInt64),
                    
                    _  => (LLVMValueRef.CreateConstIntOfArbitraryPrecision(
                            LlvmInt(i.Type.Bits), BigIntegerToULongs(i.Value, i.Type.Bits)), 
                        LlvmInt(i.Type.Bits)),
                };
            }

            case SliceConstantValue slice:
            {
                var elmtype = ConvType(slice.ElementType);
                
                var v = StoreGlobalArray(elmtype, slice.Content, true);
                var t = LLVMTypeRef.CreateArray(elmtype, (uint)slice.Content.Length);

                return (v, t);
            }
            
            default: throw new UnreachableException();
        }
    }
    
    private LLVMTypeRef CreateSlice(LLVMTypeRef elementType) => LLVMTypeRef.CreateStruct([LlvmPtr, GetNativeInt()], false);
    private LLVMTypeRef GetNativeInt() => _configuration.NativeIntegerSize switch
    {
        1 => LlvmBool,
        8 => LlvmInt8,
        16 => LlvmInt16,
        32 => LlvmInt32,
        64 => LlvmInt64,
        _ => LlvmInt(_configuration.NativeIntegerSize),
    }; 
        
    private ulong[] BigIntegerToULongs(BigInteger value, int numBits)
    {
        if (value.Sign < 0) throw new ArgumentException();

        int numWords = (numBits + 63) / 64;
        ulong[] words = new ulong[numWords];

        BigInteger remaining = value;
        for (var i = 0; i < numWords; i++)
        {
            words[i] = (ulong)(remaining & 0xFFFFFFFFFFFFFFFF);
            remaining >>= 64;
        }

        return words;
    }
    
    
    private class CompileCodeBlockCtx()
    {
        public RealizerFunction RealizerFunction;
        public LLVMValueRef LlvmFunction;
        
        public (OmegaCodeCell baseb, LLVMBasicBlockRef llvmb)[] BlockMap;
        
        public Dictionary<RealizerParameter, LLVMValueRef> Args;
        public Dictionary<int, LLVMValueRef> Registers;
    }

    private enum AccessMode { Value, Ptr }
}
