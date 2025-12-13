using System.Reflection;
using LLVMSharp.Interop;
using Tq.Module.LLVM.Compiler;
using Tq.Realizeer.Core.Program;
using Tq.Realizer.Core.Builder;
using Tq.Realizer.Core.Configuration.LangOutput;

namespace Tq.Module.LLVM.Targets;

public static class TargetWasm
{
    internal static void LlvmCompileWasm(RealizerProgram program, IOutputConfiguration config)
    {
        
        Console.WriteLine("Compiling to WASM with LLVM...");
        
        var ctx = LLVMContextRef.Create();
        
        var llvmCompiler = new LlvmCompiler(ctx, TargetsList.Wasm);
        LLVMModuleRef[] modules = [llvmCompiler.Compile(program, config)];
        
        LLVMSharp.Interop.LLVM.InitializeWebAssemblyTarget();
        LLVMSharp.Interop.LLVM.InitializeWebAssemblyTargetInfo();
        LLVMSharp.Interop.LLVM.InitializeWebAssemblyTargetMC();
        LLVMSharp.Interop.LLVM.InitializeWebAssemblyAsmParser();
        LLVMSharp.Interop.LLVM.InitializeWebAssemblyAsmPrinter();
        
        var triple = "wasm32-unknown-unknown";
        var cpu = "generic";
        var features = "";
        
        if (!LLVMTargetRef.TryGetTargetFromTriple(triple, out var llvmTarget, out var error))
            throw new Exception(error);


        var targetMachine = llvmTarget.CreateTargetMachine(
            triple,
            cpu,
            features,
            LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
            LLVMRelocMode.LLVMRelocDefault,
            LLVMCodeModel.LLVMCodeModelDefault);
        
        var exeDir = AppContext.BaseDirectory;
        
        Console.WriteLine("LLVM: Emitting artifacts...");
        
        if (!targetMachine.TryEmitToFile(modules[0], ".abs-out/main.wasm", 
                LLVMCodeGenFileType.LLVMObjectFile, out var err))
            throw new Exception(err);
        
        File.Copy(Path.Combine(exeDir, "Resources/index.html"), ".abs-out/index.html", true);
        File.Copy(Path.Combine(exeDir, "Resources/script.js"), ".abs-out/script.js", true);
        File.Copy(Path.Combine(exeDir, "Resources/lib_std.js"), ".abs-out/lib_std.js", true);
        File.Copy(Path.Combine(exeDir, "Resources/lib_env.js"), ".abs-out/lib_env.js", true);

        foreach (var i in modules) i.Dispose();
    }
}