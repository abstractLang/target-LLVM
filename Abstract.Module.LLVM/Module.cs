using Abstract.Module.Core;
using Abstract.Module.Core.Configuration;
using Abstract.Module.LLVM.Targets;
using Abstract.Realizer.Core.Configuration.LangOutput;

namespace Abstract.Module.LLVM;

public class Module : IModule
{
    public ModuleConfiguration Config { get; } = new()
    {
        Name = "LLVM",
        Description = "Provides targets for compiling with LLVM",
        
        Author = "lumi2021",
        Version = "1.0.0",
        
        Targets = [
            new ModuleLanguageTargetConfiguration
            {
                TargetName = "LLVM-WASM",
                TargetDescription = "Compiles to webassembly using LLVM",
                TargetIdentifier = "llvm-wasm",
                
                LanguageOutput = new OmegaOutputConfiguration() {
                    BakeGenerics = true,
                    UnnestMembers = true,
                    
                    MemoryUnit = 8,
                    NativeIntegerSize = 32,
                },
                CompilerInvoke = TargetWasm.LlvmCompileWasm
            },
        ]
    };
}
