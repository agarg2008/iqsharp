// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Jupyter.Core;
using Microsoft.Quantum.Circuitizer.Circuits;
using Microsoft.Quantum.Extensions.Math;
using Microsoft.Quantum.IQSharp.Common;
using Microsoft.Quantum.QsCompiler.DataTypes;
using Microsoft.Quantum.Simulation.Simulators;
using SimulatorSkeleton;
using static ImageToHtmlEncoder;

namespace Microsoft.Quantum.IQSharp.Jupyter
{
    public class CircuitMagic : AbstractMagic
    {
        public CircuitMagic(ISymbolResolver resolver, ISnippets snippets, ILogger<Workspace> logger, IReferences references) : base(
            "circuit",
            new Documentation
            {
                Summary = "Takes a given function or operation and generates the quantum circuit it represents."
            })
        {
            this.SymbolResolver = resolver;
            this.Snippets = (Snippets)snippets;
            this.Logger = logger;
            this.GlobalReferences = references;
        }

        public ISymbolResolver SymbolResolver { get; }
        public Snippets Snippets { get; }
        public ILogger Logger { get; }
        public IReferences GlobalReferences { get; }

        public override ExecutionResult Run(string input, IChannel channel) =>
            RunAsync(input, channel).Result;

        public async Task<ExecutionResult> RunAsync(string input, IChannel channel)
        {
            var (name, args) = ParseInput(input);
            var symbol = SymbolResolver.Resolve(name) as IQSharpSymbol;
            if (symbol == null) throw new InvalidOperationException($"Invalid operation name: {name}");
            var snippetsWithNoOverlap = this.Snippets.Items.Where(s => s.Elements.Select(IQSharp.Extensions.ToFullName).Contains(Snippets.SNIPPETS_NAMESPACE + "." + symbol.Name));
            var files = new List<Uri>();
            files.Add(snippetsWithNoOverlap.FirstOrDefault().Uri);
            var l = snippetsWithNoOverlap.FirstOrDefault().Elements;
            Console.WriteLine(l.Count());
            foreach (var s in l)
            {
                Console.WriteLine(s.ToFullName());

                //AssemblyInfo = Compiler.BuildFiles(files, GlobalReferences.CompilerMetadata, logger, CacheDll);

            }
            var qsNamespace = new QsCompiler.SyntaxTree.QsNamespace(NonNullable<string>.New(Snippets.SNIPPETS_NAMESPACE), ImmutableArray.Create(l), null);
            var logger = new QSharpLogger(Logger);
            var newAssembly = CompilerService.BuildAssembly(files.ToArray(), new[] { qsNamespace }, GlobalReferences.CompilerMetadata.RoslynMetadatas, logger, Path.Combine(Path.GetTempPath(), "__snippets__.dll"));
            Assembly.LoadFrom(newAssembly.Location);
            symbol = SymbolResolver.Resolve(name) as IQSharpSymbol;
            if (symbol == null) throw new InvalidOperationException($"Invalid operation name: {name}");

            var aSCIICircuitizer = new ASCIICircuitizer();
            var qsim = new CircuitizerSimulator(aSCIICircuitizer);
            qsim.DisableLogToConsole();
            qsim.OnLog += channel.Stdout;

            var value = await symbol.Operation.RunAsync(qsim, args);
            var result = qsim.Render();
            // TODO : currently we are ignoring the Render result from qsim and rendering a local file instead.
            var imageHtml = new ImageDataWrapper
            {
                imageFileName = @"C:\Users\angarg\Pictures\bad_status.PNG"
            };
            //return imageHtml.ToExecutionResult();
            return result.ToExecutionResult();
        }
    }
}
