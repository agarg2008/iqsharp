// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Jupyter.Core;
using Microsoft.Quantum.IQSharp.Common;
using Microsoft.Quantum.IQSharp.Jupyter;
using Microsoft.Quantum.Simulation.Simulators;
using SimulatorSkeleton;
using static ImageToHtmlEncoder;

namespace Microsoft.Quantum.IQSharp.Jupyter
{
    public class CircuitMagic : AbstractMagic
    {
        public CircuitMagic(ISymbolResolver resolver) : base(
            "circuit",
            new Documentation
            {
                Summary = "Takes a given function or operation and generates the quantum circuit it represents."
            })
        {
            this.SymbolResolver = resolver;
        }

        public ISymbolResolver SymbolResolver { get; }

        public override ExecutionResult Run(string input, IChannel channel) =>
            RunAsync(input, channel).Result;

        public async Task<ExecutionResult> RunAsync(string input, IChannel channel)
        {
            var (name, args) = ParseInput(input);
            var symbol = SymbolResolver.Resolve(name) as IQSharpSymbol;
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
            return imageHtml.ToExecutionResult();
        }
    }
}
