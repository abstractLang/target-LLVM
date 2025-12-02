using System.Diagnostics;
using System.Numerics;
using System.Text;
using LLVMSharp.Interop;
using Tq.Module.LLVM.Targets;
using Tq.Realizeer.Core.Program;
using Tq.Realizeer.Core.Program.Builder;
using Tq.Realizeer.Core.Program.Member;
using Tq.Realizer.Core.Builder.Execution.Omega;
using Tq.Realizer.Core.Builder.Language.Omega;
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
        this._llvmCtx = llvmCtx;
        _target = llvm;
        _intrinsincs.Clear();
        InitializeIntrinsics();
    }

    internal LLVMModuleRef Compile(RealizerProgram program, IOutputConfiguration config) 
    {
        _functions.Clear();
        _structures.Clear();
        _configuration = config;
        
        _llvmModule = _llvmCtx.CreateModuleWithName(program.Name);
        _llvmBuilder = _llvmCtx.CreateBuilder();

        
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
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i8", type);
        _intrinsincs.Add("sadd.w.ovf.i8", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i8", type);
        _intrinsincs.Add("uadd.w.ovf.i8", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt16, LlvmBool], false), [LlvmInt16, LlvmInt16]);
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i16", type);
        _intrinsincs.Add("sadd.w.ovf.i16", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i16", type);
        _intrinsincs.Add("uadd.w.ovf.i16", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt32, LlvmBool], false), [LlvmInt32, LlvmInt32]);
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i32", type);
        _intrinsincs.Add("sadd.w.ovf.i32", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i32", type);
        _intrinsincs.Add("uadd.w.ovf.i32", func);
        
        type = LlvmFunctionType(LlvmStructType([LlvmInt64, LlvmBool], false), [LlvmInt64, LlvmInt64]);
        func = _llvmModule.AddFunction("llvm.sadd.with.overflow.i64", type);
        _intrinsincs.Add("sadd.w.ovf.i64", func);
        func = _llvmModule.AddFunction("llvm.uadd.with.overflow.i64", type);
        _intrinsincs.Add("uadd.w.ovf.i64", func);
        
        switch (_target)
        {
            case TargetsList.Wasm:
            {
                type = LlvmFunctionType(LlvmInt32, []);
                func = _llvmModule.AddFunction("llvm.wasm.memory.size", type);
                _intrinsincs.Add("wasm.memory.size", func);
                
                type = LlvmFunctionType(LlvmInt32, [LlvmInt32]);
                func = _llvmModule.AddFunction("llvm.wasm.memory.grow", type);
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
        
        var vtableType = _llvmCtx.GetStructType([
            LlvmOpaquePtr, // Parent table
            GetNativeInt(), // Type size
            GetNativeInt(), // Type alignment
            CreateSlice(LlvmInt8), // StructName
            GetNativeInt(), // Vtable length
            LlvmArray(LlvmOpaquePtr, 0) // Table
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
        
        var fun = _llvmModule.AddFunction(baseFunc.Name, functype);

        if (baseFunc.ExportSymbol != null)
        {
            fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
            fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
            fun.AddTargetDependentFunctionAttr("wasm-export-name", baseFunc.ExportSymbol);
        }

        // To import:
            //fun.Linkage = LLVMLinkage.LLVMExternalLinkage;
            //fun.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;

            //fun.AddTargetDependentFunctionAttr("wasm-import-module", importedFunc.ImportDomain ?? "env");
            //fun.AddTargetDependentFunctionAttr("wasm-import-name", importedFunc.ImportSymbol!);
            
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
            
            var llvmblock = LLVMAppendBasicBlock(llvmFunction, block.Name);
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
               Locals = [],
               expectingTypeStack = []
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
        
        fields.Add(LlvmOpaquePtr);
        if (baseStruct.Extends != null)
            fields.AddRange(baseStruct.Extends.GetMembers<RealizerField>().Select(field => ConvType(field.Type!)));
        fields.AddRange(baseStruct.GetMembers<RealizerField>().Select(field => ConvType(field.Type!)));
        
        llvmStruct.StructSetBody(fields.ToArray(), false);
        
        var parentTablePointer = baseStruct.Extends == null
            ? LLVMValueRef.CreateConstPointerNull(LlvmOpaquePtr)
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
            LLVMValueRef.CreateConstArray(LlvmOpaquePtr, [])
        ], false);
    }

    
    
    private void CompileCodeBlockInstruction(LLVMBuilderRef builder, IOmegaInstruction instruction, CompileCodeBlockCtx ctx)
    {
        switch (instruction)
        {
            case Assignment @assignment:
            {
                switch (assignment.Left)
                {
                    case Member @member:
                    {
                        var ptr = CompileExecCellValue_member(builder, member, ctx, AccessMode.Write);
                        var val = CompileExecCellValue(builder, assignment.Right, ctx);
                        builder.BuildStore(val, ptr);
                    } break;

                    case Access @access:
                    {
                        var baseptr = CompileExecCellValue(builder, access.Left, ctx);
                        switch (access.Right)
                        {
                            case Member { Node: RealizerField, Node.Static: false, Node.Parent: RealizerStructure } @m:
                            {
                                var field = (RealizerField)m.Node;
                                var structype = ConvType(new NodeTypeReference((RealizerStructure)field.Parent!));
                                baseptr = builder.BuildStructGEP2(structype, baseptr, field.Index);
                            } break;

                            default: throw new UnreachableException();
                        }

                        var val = CompileExecCellValue(builder, assignment.Right, ctx);
                        builder.BuildStore(val, baseptr);
                    } break;
                        
                    default: throw new UnreachableException();
                };
            } break;

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
    private LLVMValueRef CompileExecCellValue(LLVMBuilderRef builder, IOmegaExpression instExpression, CompileCodeBlockCtx ctx,
        AccessMode access = AccessMode.Read)
    {
        switch (instExpression)
        {
            case Add @add:
                return builder.BuildAdd(CompileExecCellValue(builder, add.Left, ctx),
                    CompileExecCellValue(builder, add.Right, ctx));
            
            // Sub @sub => return builder.BuildSub(
            //        CompileExecCellValue(builder, sub.Left, ctx),
            //        CompileExecCellValue(builder, sub.Right, ctx));
            
            case Mul @mul:
                return builder.BuildMul(CompileExecCellValue(builder, mul.Left, ctx),
                    CompileExecCellValue(builder, mul.Right, ctx));

            case Access @acc:
            {
                var baseptr = CompileExecCellValue(builder, acc.Left, ctx);
                switch (acc.Right)
                {
                    case Member { Node: RealizerField @f, Node.Static: false }:
                    {
                        var fieldptr = builder.BuildStructGEP2(
                            _structures[(RealizerStructure)f.Parent!].type,
                            baseptr, f.Index+1);
                        
                        return access == AccessMode.Read
                            ? builder.BuildLoad2(ConvType(f.Type), fieldptr)
                            : fieldptr;
                    }

                    case Member { Node: RealizerProperty @p, Node.Static: false }:
                        switch (access)
                        {
                            case AccessMode.Read:
                            {
                                var (ft, fn) = _functions[p.Getter!];
                                return builder.BuildCall2(ft, fn, [baseptr]);
                            }
                            case AccessMode.Write: throw new NotImplementedException();
                            default: throw new ArgumentOutOfRangeException();
                        }

                    case Call { Callable: RealizerFunction @f } @c:
                    {
                        var (ft, fn) = _functions[f];
                        var args = new LLVMValueRef[f.Parameters.Length];
                        args[0] = baseptr;
                        
                        for (int i = 0; i < c.Arguments.Length; i++)
                            args[i+1] = CompileExecCellValue(builder, c.Arguments[i], ctx);
                        
                        return builder.BuildCall2(ft, fn, args);
                    }
                    
                    default: throw new UnreachableException();
                }
            }
            
            case Constant @const:
                return @const.Value switch
                {
                    IntegerConstantValue @icv => LLVMValueRef.CreateConstInt(GetIntType(icv.BitSize),
                        unchecked((ulong)(Int128)icv.Value)),

                    NullConstantValue @ncv => LLVMValueRef.CreateConstIntToPtr(
                        LLVMValueRef.CreateConstInt(GetNativeInt(), 0), LlvmOpaquePtr),

                    SliceConstantValue @slice => StoreGlobalArray(ConvType(slice.ElementType), slice.Content, true),
                    
                    _ => throw new UnreachableException(),
                };
            
            case Call @call:
            {
                var fn = CompileExecCellValue_callable(builder, call.Callable, ctx);
                var args = call.Arguments.Select(e => CompileExecCellValue(builder, e, ctx)).ToArray();
                
                return builder.BuildCall2(fn.ftype, fn.fun, args);
            }
            
            case Member @m: return CompileExecCellValue_member(builder, m, ctx);
            
            case Argument @a: return ctx.Args[a.Parameter];
            
            case Self: return ctx.Args[ctx.RealizerFunction.Parameters[0]];
            
            default: throw new UnreachableException();
        }
    }

    private LLVMValueRef CompileExecCellValue_member(LLVMBuilderRef builder, Member member, CompileCodeBlockCtx ctx,
        AccessMode access = AccessMode.Read)
    {
        return member.Node switch
        {
            RealizerField { Static: true } f => access == AccessMode.Read
                ? builder.BuildLoad2(ConvType(f.Type), _staticFields[f].fobj) : _staticFields[f].fobj,
            
            _ => throw new UnreachableException()
        };
    }

    private (LLVMTypeRef ftype, LLVMValueRef fun) CompileExecCellValue_callable(LLVMBuilderRef builder,
        IOmegaCallable callable, CompileCodeBlockCtx ctx, AccessMode access = AccessMode.Read)
    {
        return callable switch
        {
            Member { Node: RealizerFunction @f } => _functions[f],
            
            _ => throw new UnreachableException()
        };
    }



    private LLVMTypeRef GetIntType(ushort bitsize)
    {
        return bitsize switch
        {
            0 => GetNativeInt(),
            1 => LlvmBool,
            8 => LlvmInt8,
            16 => LlvmInt16,
            32 => LlvmInt32,
            64 => LlvmInt64,
            _ => LlvmInt(bitsize),
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
            ReferenceTypeReference @refe => LlvmOpaquePtr,
            NullableTypeReference @refe => refe.Subtype is ReferenceTypeReference
                    ? LlvmOpaquePtr
                    : LlvmStructType([ConvType(refe.Subtype), LlvmBool], true),
            
            _ => throw new UnreachableException()
        };
        
    }

    private uint BitsToMemUnit(uint? size) => Math.Max(1, (size ?? _configuration.NativeIntegerSize) / _configuration.MemoryUnit);

    private uint AlignOf(RealizerStructure struc) => BitsToMemUnit(@struc.Alignment);
    private uint AlignOf(TypeReference? type)
    {
        if (type == null) return 0;
        return type switch
        {
            NodeTypeReference { TypeReference: RealizerStructure @struct } => BitsToMemUnit(@struct.Alignment),
            NodeTypeReference { TypeReference: RealizerTypedef @typedef } => AlignOf(typedef.BackingType),
            
            IntegerTypeReference @intt => BitsToMemUnit(@intt.Bits),
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

        for (var i = 0; i < strdata.Length; i++) array[i] = new IntegerConstantValue(8, strdata[i]);

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
                return (LLVMValueRef.CreateConstNull(LlvmOpaquePtr), LlvmOpaquePtr);
            
            case IntegerConstantValue i:
            {
                var value = unchecked((ulong)(Int128)i.Value);
                
                return i.BitSize switch
                {
                    0 => (LLVMValueRef.CreateConstInt(GetNativeInt(), value, true), LlvmBool),
                    1  => (LLVMValueRef.CreateConstInt(LlvmBool, value, true), LlvmBool),
                    8  => (LLVMValueRef.CreateConstInt(LlvmInt8, value, true), LlvmInt8),
                    16 => (LLVMValueRef.CreateConstInt(LlvmInt16, value, true), LlvmInt16),
                    32 => (LLVMValueRef.CreateConstInt(LlvmInt32, value, true), LlvmInt32),
                    64 => (LLVMValueRef.CreateConstInt(LlvmInt64, value, true), LlvmInt64),
                    
                    _  => (LLVMValueRef.CreateConstIntOfArbitraryPrecision(
                            LlvmInt(i.BitSize), BigIntegerToULongs(i.Value, i.BitSize)),
                        LlvmInt(i.BitSize)),
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
    
    private LLVMTypeRef CreateSlice(LLVMTypeRef elementType) => LLVMTypeRef.CreateStruct([
        LlvmPtr(elementType), GetNativeInt()], false);
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
        public Dictionary<int, LLVMValueRef> Locals;

        public Stack<TypeReference> expectingTypeStack;
    }

    private enum AccessMode { Read, Write }
    
}
