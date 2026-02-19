using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RimWorldCodeRag.Indexer;

/// <summary>
/// Helper class to create a Roslyn CSharpCompilation from source files and DLL references.
/// The Compilation provides access to the SemanticModel, which is required for accurate
/// symbol resolution (overload resolution, static member resolution, etc.).
/// </summary>
public static class RoslynProject
{
    /// <summary>
    /// Creates a CSharpCompilation from source files in a directory and referenced DLLs.
    /// </summary>
    /// <param name="sourceDirectory">Directory containing C# source files to compile.</param>
    /// <param name="dllPaths">Paths to DLLs that should be referenced (RimWorld, Unity, etc.).</param>
    /// <returns>A Compilation object that can provide SemanticModels for each syntax tree.</returns>
    public static CSharpCompilation CreateCompilation(string sourceDirectory, IEnumerable<string>? dllPaths = null)
    {
        Console.WriteLine("[Roslyn] Finding C# source files...");
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);
        Console.WriteLine($"[Roslyn] Found {sourceFiles.Length} C# files");

        Console.WriteLine("[Roslyn] Parsing source files...");
        var syntaxTrees = sourceFiles
            .Select(file => 
            {
                try
                {
                    var text = File.ReadAllText(file);
                    return CSharpSyntaxTree.ParseText(
                        text, 
                        new CSharpParseOptions(LanguageVersion.Preview), 
                        path: file
                    );
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Roslyn] Warning: Could not parse {file}: {ex.Message}");
                    return null;
                }
            })
            .Where(tree => tree != null)
            .Cast<SyntaxTree>()
            .ToList();

        Console.WriteLine($"[Roslyn] Parsed {syntaxTrees.Count} syntax trees");

        Console.WriteLine("[Roslyn] Loading metadata references...");
        var references = new List<MetadataReference>();
        
        // Add reference to core .NET libraries
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var coreAssemblies = new[]
        {
            typeof(object).Assembly.Location,                          // System.Private.CoreLib
            Path.Combine(runtimeDir, "System.Runtime.dll"),
            Path.Combine(runtimeDir, "System.Collections.dll"),
            Path.Combine(runtimeDir, "System.Linq.dll"),
            Path.Combine(runtimeDir, "System.Console.dll"),
            Path.Combine(runtimeDir, "netstandard.dll"),
        };

        foreach (var assembly in coreAssemblies)
        {
            if (File.Exists(assembly))
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        // Add user-specified DLL references (RimWorld, Unity, etc.)
        if (dllPaths != null)
        {
            foreach (var path in dllPaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(path));
                        Console.WriteLine($"[Roslyn] Loaded reference: {Path.GetFileName(path)}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Roslyn] Warning: Could not load reference {path}: {ex.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine($"[Roslyn] Warning: Could not find reference DLL: {path}");
                }
            }
        }

        Console.WriteLine($"[Roslyn] Loaded {references.Count} metadata references");

        Console.WriteLine("[Roslyn] Creating C# compilation...");
        var compilation = CSharpCompilation.Create(
            "RimWorldAnalysis",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                // Suppress warnings about missing references - we're just using this for symbol resolution
                generalDiagnosticOption: ReportDiagnostic.Suppress
            )
        );

        // NOTE: We intentionally do NOT call compilation.GetDiagnostics() here
        // because it can cause StackOverflowException when processing RimWorld code
        // with missing external references (Unity, etc.). The SemanticModel will still
        // work for symbol resolution even without full diagnostic analysis.

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Roslyn] Compilation created with {syntaxTrees.Count} syntax trees and {references.Count} references");
        Console.ResetColor();

        return compilation;
    }

    /// <summary>
    /// Creates a CSharpCompilation from a list of source file paths.
    /// </summary>
    /// <param name="sourceFiles">Paths to individual C# source files.</param>
    /// <param name="dllPaths">Paths to DLLs that should be referenced.</param>
    /// <returns>A Compilation object.</returns>
    public static CSharpCompilation CreateCompilationFromFiles(IEnumerable<string> sourceFiles, IEnumerable<string>? dllPaths = null)
    {
        var syntaxTrees = sourceFiles
            .Select(file =>
            {
                try
                {
                    var text = File.ReadAllText(file);
                    return CSharpSyntaxTree.ParseText(
                        text,
                        new CSharpParseOptions(LanguageVersion.Preview),
                        path: file
                    );
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Roslyn] Warning: Could not parse {file}: {ex.Message}");
                    return null;
                }
            })
            .Where(tree => tree != null)
            .Cast<SyntaxTree>()
            .ToList();

        var references = new List<MetadataReference>();
        
        // Add core .NET references
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var coreAssemblies = new[]
        {
            typeof(object).Assembly.Location,
            Path.Combine(runtimeDir, "System.Runtime.dll"),
            Path.Combine(runtimeDir, "System.Collections.dll"),
            Path.Combine(runtimeDir, "System.Linq.dll"),
            Path.Combine(runtimeDir, "netstandard.dll"),
        };

        foreach (var assembly in coreAssemblies)
        {
            if (File.Exists(assembly))
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        // Add user-specified DLL references
        if (dllPaths != null)
        {
            foreach (var path in dllPaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(path));
                    }
                    catch { /* ignore */ }
                }
            }
        }

        return CSharpCompilation.Create(
            "RimWorldAnalysis",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }

    /// <summary>
    /// Finds all DLLs in a directory that might be relevant references.
    /// </summary>
    /// <param name="directory">Directory to search for DLLs.</param>
    /// <returns>List of DLL paths.</returns>
    public static IEnumerable<string> FindDllsInDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var dll in Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories))
        {
            yield return dll;
        }
    }
}
