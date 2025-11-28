using Tq.Module.LLVM.Targets;
using Tq.Realizer.Core.Configuration;
using Tq.Realizer.Core.Configuration.LangOutput;

namespace Tq.Module.LLVM;

public class Module: IModule
{
    public ModuleConfiguration Config { get; } = new()
    {
        Name = "LLVM",
        Description = "Provides targets for compiling with LLVM",
        
        Author = "lumi2021",
        Version = "1.0.0",
        
        Targets = [
            new TargetConfiguration
            {
                TargetName = "LLVM-WASM",
                TargetDescription = "Compiles to webassembly using LLVM",
                TargetIdentifier = "llvm-wasm",
                
                LanguageOutput = new OmegaOutputConfiguration() {
                    BakeGenerics = true,
                    UnnestMembersOption = UnnestMembersOptions.NoNamespaces
                                        | UnnestMembersOptions.ForceStaticFunctions,
                    
                    MemoryUnit = 8,
                    NativeIntegerSize = 32,
                    
                    GenericAllowedFeatures = GenericAllowedFeatures.None, 
                    OmegaAllowedFeatures = OmegaAllowedFeatures.None,
                },
                
                CompilerInvoke = TargetWasm.LlvmCompileWasm
            },
        ]
    };
}
