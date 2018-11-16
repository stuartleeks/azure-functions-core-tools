﻿using Colors.Net;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.Interfaces;
using static Azure.Functions.Cli.Common.OutputTheme;

using Buildalyzer;
using Buildalyzer.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

using Microsoft.Azure.WebJobs;
using Azure.Functions.Cli.Actions.LocalActions;

namespace Azure.Functions.Cli.Actions.DurableActions
{
    [Action(Name = "create-helpers", Context = Context.Durable, HelpText = "Create or update generated durable functions helpers")]
    internal partial class CreateDurableFunctionsHelpersAction : BaseAction
    {
        private readonly ISecretsManager _secretsManager;

        public CreateDurableFunctionsHelpersAction(ISecretsManager secretsManager)
        {
            _secretsManager = secretsManager;
        }
        public override async Task RunAsync()
        {
            var workerRuntime = WorkerRuntimeLanguageHelper.GetCurrentWorkerRuntimeLanguage(_secretsManager);

            ColoredConsole.WriteLine(AdditionalInfoColor($"Runtime: {workerRuntime}"));

            if (workerRuntime != WorkerRuntime.dotnet)
            {
                ColoredConsole.WriteLine(ErrorColor($"create-durable-helpers does not support runtime: {workerRuntime}"));
                return;
            }

            var csproj = DotnetHelpers.GetCsproj();
            ColoredConsole.WriteLine(AdditionalInfoColor($"Using csproj: {csproj}"));



            var analyzerManager = new AnalyzerManager(new AnalyzerManagerOptions());
            var projectAnalyzer = analyzerManager.GetProject(csproj);
            var workspace = projectAnalyzer.GetWorkspace();

            var project = workspace.CurrentSolution.Projects.FirstOrDefault(p => p.FilePath == csproj);


            var methodsBySemanticModel = await GetActivityFunctionMethods(project);

            var compilationUnitSyntax = GenerateCode(project, methodsBySemanticModel);


            var generatedCode = compilationUnitSyntax.ToString();

            var generatedFilename = "DurableFunctionsHelpers.generated.cs";
            var csprojFolder = Path.GetDirectoryName(csproj);
            var generatedFilePath = System.IO.Path.Combine(csprojFolder, generatedFilename);
            ColoredConsole.WriteLine(AdditionalInfoColor($"Writing generated code to {generatedFilePath}"));
            await FileSystemHelpers.WriteAllTextToFileAsync(generatedFilePath, generatedCode);
        }

        private static bool MethodHasActivityTriggerParameter(IMethodSymbol method)
        {
            return method.Parameters.Any(ParameterIsActivityTrigger);
        }

        private static bool ParameterIsActivityTrigger(IParameterSymbol parameter)
        {
            return parameter.GetAttributes()
                    .Any(a => a.AttributeClass.ToString() == "Microsoft.Azure.WebJobs.ActivityTriggerAttribute");
        }

        private static async Task<IEnumerable<IMethodSymbol>> GetActivityFunctionMethods(Project project)
        {
            var attributeType = typeof(FunctionNameAttribute);
            string[] attributeNames;
            var tempAttributeName = attributeType.Name;
            if (tempAttributeName.EndsWith("Attribute", StringComparison.InvariantCultureIgnoreCase))
            {
                // TODO - revisit as this doesn't handle qualified names, other attributes with the same name, ...
                attributeNames = new[] { tempAttributeName, tempAttributeName.Substring(0, tempAttributeName.Length - "Attribute".Length) };
            }
            else
            {
                attributeNames = new[] { tempAttributeName };
            }

            bool attributeMatchesName(AttributeSyntax attribute)
            {
                SyntaxToken attributeIdentifier;
                if (attribute.Name is IdentifierNameSyntax ins)
                {
                    attributeIdentifier = ins.Identifier;
                }
                else if (attribute.Name is QualifiedNameSyntax qns)
                {
                    attributeIdentifier = qns.Right.Identifier;
                }
                else
                {
                    return false;
                }

                var attributeName = attributeIdentifier.ValueText;
                return attributeNames.Any(n =>
                {
                    return attributeName?.EndsWith(n, StringComparison.InvariantCultureIgnoreCase) ?? false;
                });
            }
            var methods = (await project.Documents
                    .Select(async document =>
                    {
                        var root = await document.GetSyntaxRootAsync();
                        var model = await document.GetSemanticModelAsync();

                        IEnumerable<IMethodSymbol> matchingMethods = root.DescendantNodes()
                            .OfType<AttributeSyntax>()
                            .Where(attributeMatchesName)
                            // Project attribute -> attribute list -> method                    
                            .Select(a => a.Parent.Parent as MethodDeclarationSyntax)
                            // filter non-methods
                            .Where(m => m != null)
                            // project to method declaration
                            .Select(m => model.GetDeclaredSymbol(m))
                            // filter to just methods with attribute of the correct type
                            .Where(m => m.GetAttributes().Any(a => a.AttributeClass.ToString() == attributeType.FullName))
                            .Where(MethodHasActivityTriggerParameter);
                        return matchingMethods;
                    })
                    .WaitAllAndUnwrap()
                )
                .SelectMany(o => o);
            return methods;
        }
    }
}
