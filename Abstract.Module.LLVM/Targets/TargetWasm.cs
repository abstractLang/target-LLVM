using System.Reflection;
using Abstract.Module.LLVM.Compiler;
using Abstract.Realizer.Builder;
using LLVMSharp.Interop;

namespace Abstract.Module.LLVM.Targets;

public static class TargetWasm
{
    internal static void LlvmCompileWasm(ProgramBuilder program)
    {
        
        Console.WriteLine("Compiling to WASM with LLVM...");
        var module = LlvmCompiler.Compile(program);
        
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
        
        var exePath = Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? "";
        
        Console.WriteLine("LLVM: Emitting artifact...");
        targetMachine.EmitToFile(module, ".abs-out/main.wasm", LLVMCodeGenFileType.LLVMObjectFile);
        File.Copy(Path.Combine(exeDir, "Resources/index.html"), ".abs-out/index.html", true);
        File.Copy(Path.Combine(exeDir, "Resources/script.js"), ".abs-out/script.js", true);
        File.Copy(Path.Combine(exeDir, "Resources/std.js"), ".abs-out/std.js", true);
    }
}