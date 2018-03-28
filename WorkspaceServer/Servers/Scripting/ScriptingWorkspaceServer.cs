﻿using System;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Clockwise;
using Microsoft.CodeAnalysis;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Scripting;
using Pocket;
using WorkspaceServer.Models;
using WorkspaceServer.Models.Completion;
using WorkspaceServer.Models.Execution;
using WorkspaceServer.Models.SingatureHelp;
using WorkspaceServer.Transformations;
using static Pocket.Logger<WorkspaceServer.Servers.Scripting.ScriptingWorkspaceServer>;
using static WorkspaceServer.Servers.WorkspaceServer;
using Workspace = WorkspaceServer.Models.Execution.Workspace;

namespace WorkspaceServer.Servers.Scripting
{
    public class ScriptingWorkspaceServer : IWorkspaceServer
    {
        private readonly BufferInliningTransformer _transformer = new BufferInliningTransformer();

        public async Task<RunResult> Run(Workspace workspace, Budget budget = null)
        {
            budget = budget ?? new Budget();

            using (var operation = Log.OnEnterAndConfirmOnExit())
            using (var console = await ConsoleOutput.Capture(budget))
            {
                workspace = await _transformer.TransformAsync(workspace, budget);

                if (workspace.Files.Count != 1)
                {
                    throw new ArgumentException($"{nameof(workspace)} should have exactly one source file.");
                }

                var options = CreateOptions(workspace);
               
                ScriptState<object> state = null;
                Exception userException = null;

                var buffer = new StringBuilder(workspace.GetSourceFiles().Single().Text.ToString());

                try
                {
                    state = await Run(buffer, options, budget);

                    if (console.IsEmpty())
                    {
                        state = await EmulateConsoleMainInvocation(state, buffer, options, budget);
                    }

                    budget.RecordEntry(UserCodeCompletedBudgetEntryName);
                }
                catch (CompilationErrorException ex)
                {
                    userException = ex;

                    Console.WriteLine(
                        string.Join(Environment.NewLine,
                                    ex.Diagnostics
                                      .Select(d => d.ToString())));
                }
                catch (Exception ex)
                {
                    userException = ex;
                }

                budget.RecordEntryAndThrowIfBudgetExceeded();

                var processeddiagnostics = await GetDiagnostics(workspace, options);
                var diagnostics = processeddiagnostics.Select(e => e.Diagnostic).ToArray();
                var output = console.StandardOutput
                                    .Replace("\r\n", "\n")
                                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                output = ProcessOutputLines(output,
                                            processeddiagnostics
                                                .Where(e => e.Diagnostic.Severity == DiagnosticSeverity.Error)
                                                .Select(e => e.ErrorMessage)
                                                .ToArray());
                var result = new RunResult(
                    succeeded: !userException.IsConsideredRunFailure(),
                    output: output,
                    exception: (userException ?? state?.Exception).ToDisplayString(),
                    diagnostics: diagnostics);

                operation.Complete(result, budget);

                return result;
            }
        }

        private string[] ProcessOutputLines(string[] output, string[] errormessages)
        {
            var filter = output.Where(IsNotDisagnostic);

            return filter.Concat(errormessages).ToArray();
        }


        private bool IsNotDisagnostic(string line)
        {
            var filter = new Regex(@"^(?<location>\(\d+,\d+\):)\s*(?<level>\S+)\s*(?<code>[A-Z]{2}\d+:)(?<message>.+)", RegexOptions.Compiled);
            return !filter.IsMatch(line);
        }

        private static ScriptOptions CreateOptions(Workspace request) =>
            ScriptOptions.Default
                         .AddReferences(GetReferenceAssemblies())
                         .AddImports(GetDefultUsings().Concat(request.Usings));

        private async Task<(SerializableDiagnostic Diagnostic, string ErrorMessage)[]> GetDiagnostics(
            Workspace workspace,
            ScriptOptions options, Budget budget = null)
        {
            budget = budget ?? new Budget();

            var processor = new BufferInliningTransformer();
            var processed = await processor.TransformAsync(workspace, budget);
            var viewPorts = processor.ExtractViewPorts(processed);
            var sourceFile = processed.GetSourceFiles().Single();
            var code = sourceFile.Text.ToString();
            var sourceDiagnostics = CSharpScript.Create(code, options)
                .GetCompilation()
                .GetDiagnostics()
                .Where(d => d.Id != "CS7022");
            budget.RecordEntry();
            return DiagnosticTransformer.ReconstructDiagnosticLocations(
                    sourceDiagnostics,
                    viewPorts,
                    BufferInliningTransformer.PaddingSize)
                .ToArray();

        }

        private static Task<ScriptState<object>> Run(
            StringBuilder buffer,
            ScriptOptions options,
            Budget budget) =>
            Task.Run(() =>
                         CSharpScript.RunAsync(
                             buffer.ToString(),
                             options))
                .CancelIfExceeds(budget, () => null);

        private static Assembly[] GetReferenceAssemblies() =>
            new[]
            {
                typeof(object).GetTypeInfo().Assembly,
                typeof(Enumerable).GetTypeInfo().Assembly,
                typeof(Console).GetTypeInfo().Assembly
            };

        private static string[] GetDefultUsings() =>
            new[] { "System", "System.Linq", "System.Collections.Generic" };

        public async Task<CompletionResult> GetCompletionList(CompletionRequest request, Budget budget = null)
        {
            budget = budget ?? new Budget();
            using (Log.OnExit())
            {
                var (document, position) = await GenerateDocumentAndPosition(request, budget);
                var service = CompletionService.GetService(document);

                var completionList = await service.GetCompletionsAsync(document, position);

                return new CompletionResult(
                    items: completionList.Items.Select(item => item.ToModel()).ToArray());
            }
        }

        public async Task<SignatureHelpResponse> GetSignatureHelp(SignatureHelpRequest request, Budget budget = null)
        {
            budget = budget ?? new Budget();
            using (Log.OnExit())
            {
                var (document, position) = await GenerateDocumentAndPosition(request, budget);
                var response = await SignatureHelpService.GetSignatureHelp(document, position, budget);
                return response;
            }
        }

        private static async Task<(Document document, int position)> GenerateDocumentAndPosition(WorkspacePositionRequest request, Budget budget)
        {
            var processor = new BufferInliningTransformer();
            var workspace = await processor.TransformAsync(request.Workspace, budget);

            if (workspace.Files.Count != 1)
            {
                throw new ArgumentException($"{nameof(request)} should have exactly one source file.");
            }

            var code = workspace.Files.Single().Text;
            var position = workspace.Buffers.First(b => b.Id == request.ActiveBufferId).Position + request.Position;

            var sourceFile = SourceFile.Create(code, position);
            var document = CreateDocument(sourceFile);
            return (document, position);
        }

        private static Document CreateDocument(SourceFile sourceFile)
        {
            var workspace = new AdhocWorkspace(MefHostServices.DefaultHost);

            var projectId = ProjectId.CreateNewId("ScriptProject");

            var metadataReferences = GetReferenceAssemblies()
                .Select(a => MetadataReference.CreateFromFile(a.Location));

            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: GetDefultUsings());

            var projectInfo = ProjectInfo.Create(
                projectId,
                version: VersionStamp.Create(),
                name: "ScriptProject",
                assemblyName: "ScriptProject",
                language: LanguageNames.CSharp,
                compilationOptions: compilationOptions,
                metadataReferences: metadataReferences);

            workspace.AddProject(projectInfo);

            var documentId = DocumentId.CreateNewId(projectId, "ScriptDocument");

            var documentInfo = DocumentInfo.Create(documentId,
                                                   name: "ScriptDocument",
                                                   sourceCodeKind: SourceCodeKind.Script);

            workspace.AddDocument(documentInfo);

            var solution = workspace.CurrentSolution
                                    .WithDocumentText(documentId, sourceFile.Text);

            workspace.TryApplyChanges(solution);

            return workspace.CurrentSolution.GetDocument(documentId);
        }

        private static async Task<ScriptState<object>> EmulateConsoleMainInvocation(
            ScriptState<object> state,
            StringBuilder buffer,
            ScriptOptions options,
            Budget budget)
        {
            var script = state.Script;
            var compiled = script.Compile();

            if (compiled.FirstOrDefault(d => d.Descriptor.Id == "CS7022")
                    is Diagnostic noEntryPointWarning &&
                EntryPointType()
                    is IMethodSymbol entryPointMethod)
            {
                // e.g. warning CS7022: The entry point of the program is global script code; ignoring 'Program.Main()' entry point.

                // add a line of code to call Main using reflection
                buffer.AppendLine(
                    $@"
typeof({entryPointMethod.ContainingType.Name})
    .GetMethod(""Main"",
               System.Reflection.BindingFlags.Static |
               System.Reflection.BindingFlags.NonPublic |
               System.Reflection.BindingFlags.Public)
    .Invoke(null, {ParametersForMain()});");

                state = await Run(buffer, options, budget);
            }

            return state;

            IMethodSymbol EntryPointType() =>
                EntryPointFinder.FindEntryPoint(
                    script.GetCompilation().GlobalNamespace);

            string ParametersForMain() => entryPointMethod.Parameters.Any()
                                              ? "new object[]{ new string[0] }"
                                              : "null";
        }

        public async Task<DiagnosticResult> GetDiagnostics(Workspace request, Budget budget = null) =>
            new DiagnosticResult((await GetDiagnostics(request, CreateOptions(request), budget)).Select(e => e.Diagnostic).ToArray());
    }
}
