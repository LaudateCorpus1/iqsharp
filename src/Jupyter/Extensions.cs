// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.Quantum.IQSharp.Common;
using Microsoft.Quantum.QsCompiler.SyntaxTokens;
using Microsoft.Quantum.QsCompiler.SyntaxTree;
using Microsoft.Quantum.Simulation.Common;
using Microsoft.Quantum.Simulation.Simulators;
using Newtonsoft.Json;
using NumSharp;

namespace Microsoft.Quantum.IQSharp.Jupyter;

/// <summary>
///      Extension methods to be used with various IQ# and Jupyter objects.
/// </summary>
public static class Extensions
{
    /// <summary>
    ///     Given a configuration source, applies an action if that
    ///     configuration source defines a value for a particular
    ///     configuration key.
    /// </summary>
    internal static IConfigurationSource ApplyConfiguration<T>(
        this IConfigurationSource configurationSource,
        string keyName, Action<T> action
    )
    {
        if (configurationSource.Configuration.TryGetValue(keyName, out var value))
        {
            if (value.ToObject<T>() is T obj)
                action(obj);
        }
        return configurationSource;
    }

    private static readonly IImmutableList<(long, string)> byteSuffixes = new List<(long, string)>
    {
        (1L << 50, "PiB"),
        (1L << 40, "TiB"),
        (1L << 30, "GiB"),
        (1L << 20, "MiB"),
        (1L << 10, "KiB")
    }.ToImmutableList();

    /// <summary>
    ///      Given a number of bytes, formats that number as a human
    ///      readable string by appending unit suffixes (i.e.: indicating
    ///      kilobytes, megabytes, etc.).
    /// </summary>
    /// <param name="nBytes">A number of bytes to be formatted.</param>
    /// <returns>
    ///     The number of bytes formatted as a human-readable string.
    /// </returns>
    public static string ToHumanReadableBytes(this long nBytes)
    {
        foreach (var (scale, suffix) in byteSuffixes)
        {
            if (nBytes >= scale)
            {
                var coefficient = ((double)nBytes) / scale;
                return $"{coefficient.ToString("0.###")} {suffix}";
            }
        }
        // Fall through to just bytes.
        return $"{nBytes} B";
    }

    /// <summary>
    ///     An enumerable source of the significant amplitudes of this state
    ///     vector and their labels, where significance and labels are
    ///     defined by the values loaded from <paramref name="configurationSource" />.
    /// </summary>
    public static IEnumerable<(Complex, string)> SignificantAmplitudes(
        this CommonNativeSimulator.DisplayableState state, 
        IConfigurationSource configurationSource
    ) => state.SignificantAmplitudes(
        configurationSource.BasisStateLabelingConvention,
        configurationSource.TruncateSmallAmplitudes,
        configurationSource.TruncationThreshold
    );

    /// <summary>
    ///     Adds functionality to a given quantum simulator to display
    ///     diagnostic output and stack traces for exceptions.
    /// </summary>
    /// <param name="simulator">
    ///     The simulator to be augmented with stack trace display functionality.
    /// </param>
    /// <param name="channel">
    ///     The Jupyter display channel to be used to display stack traces.
    /// </param>
    /// <returns>
    ///     The value of <paramref name="simulator" />.
    /// </returns>
    public static T WithStackTraceDisplay<T>(this T simulator, IChannel channel)
    where T: SimulatorBase
    {
        simulator.DisableExceptionPrinting();
        simulator.OnException += (exception, stackTrace) =>
        {
            channel.Display(new DisplayableException
            {
                Exception = exception,
                StackTrace = stackTrace
            });
        };
        return simulator;
    }

    /// <summary>
    ///      Retrieves and JSON-decodes the value for the given parameter name.
    /// </summary>
    /// <typeparam name="T">
    ///      The expected type of the decoded parameter.
    /// </typeparam>
    /// <param name="parameters">
    ///     Dictionary from parameter names to JSON-encoded values.
    /// </param>
    /// <param name="parameterName">
    ///     The name of the parameter to be decoded.
    /// </param>
    /// <param name="decoded">
    ///     The returned value of the parameter once it has been decoded.
    /// </param>
    /// <param name="defaultValue">
    ///      The default value to be returned if no parameter with the
    ///      name <paramref name="parameterName"/> is present in the
    ///      dictionary.
    /// </param>
    public static bool TryDecodeParameter<T>(this Dictionary<string, string> parameters, string parameterName, out T decoded, T defaultValue = default)
    where T: struct
    {
        try
        {
            decoded = (T)(parameters.DecodeParameter(parameterName, typeof(T), defaultValue)!);
            return true;
        }
        catch
        {
            decoded = default;
            return false;
        }
    }

    /// <summary>
    ///      Retrieves and JSON-decodes the value for the given parameter name.
    /// </summary>
    /// <typeparam name="T">
    ///      The expected type of the decoded parameter.
    /// </typeparam>
    /// <param name="parameters">
    ///     Dictionary from parameter names to JSON-encoded values.
    /// </param>
    /// <param name="parameterName">
    ///     The name of the parameter to be decoded.
    /// </param>
    /// <param name="defaultValue">
    ///      The default value to be returned if no parameter with the
    ///      name <paramref name="parameterName"/> is present in the
    ///      dictionary.
    /// </param>
    public static T? DecodeParameter<T>(this Dictionary<string, string> parameters, string parameterName, T? defaultValue = default)
    {
        return (T?)(parameters.DecodeParameter(parameterName, typeof(T), defaultValue));
    }

    /// <summary>
    ///      Retrieves and JSON-decodes the value for the given parameter name.
    /// </summary>
    /// <param name="parameters">
    ///     Dictionary from parameter names to JSON-encoded values.
    /// </param>
    /// <param name="parameterName">
    ///     The name of the parameter to be decoded.
    /// </param>
    /// <param name="type">
    ///      The expected type of the decoded parameter.
    /// </param>
    /// <param name="defaultValue">
    ///      The default value to be returned if no parameter with the
    ///      name <paramref name="parameterName"/> is present in the
    ///      dictionary.
    /// </param>
    public static object? DecodeParameter(this Dictionary<string, string> parameters, string parameterName, Type type, object? defaultValue = default)
    {
        if (!parameters.TryGetValue(parameterName, out string parameterValue))
        {
            return defaultValue!;
        }

        // If this is a JSON-formatted parameter that's being deserialized into a dictionary, remove the extra quotes and backslashes.   
        if (type == typeof(ImmutableDictionary<string, string>) && parameterValue.Length > 1)
        {
            // Jupyter wraps JSON in double quotes. Make sure that's what we have...
            if (parameterValue[0] == '"' && parameterValue[parameterValue.Length - 1] == '"')
            {
                parameterValue = parameterValue.Substring(1, parameterValue.Length - 2);    // Strip off the enclosing quotes
                parameterValue = parameterValue.Replace("\\\"", "\"");                      // Don't escape the interior quotes
            }

            // This is a temporary fix in order to send JSON encoded data as strings to the Azure submission client
            var intermediate = JsonConvert.DeserializeObject<ImmutableDictionary<string, object>>(parameterValue);
            if (intermediate is null)
            {
                return defaultValue;
            }
            else
            {
                return intermediate.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToString());
            }
        }

        return JsonConvert.DeserializeObject(parameterValue, type) ?? defaultValue;
    }

    /// <summary>
    /// Makes the channel to start capturing the Console Output.
    /// Returns the current TextWriter in the Console so callers can set it back.
    /// </summary>
    /// <param name="channel">The channel to redirect console output to.</param>
    /// <returns>The current System.Console.Out</returns>
    public static System.IO.TextWriter? CaptureConsole(this IChannel channel)
    {
        var current = System.Console.Out;
        System.Console.SetOut(new ChannelWriter(channel));
        return current;
    }

    internal static string AsLaTeXMatrixOfComplex(this NDArray array) =>
        // NB: Assumes 𝑛 × 𝑛 × 2 array, where the trailing index is
        //     [real, imag].
        // TODO: Consolidate with logic at:
        //       https://github.com/microsoft/QuantumLibraries/blob/505fc27383c9914c3e1f60fb63d0acfe60b11956/Visualization/src/DisplayableUnitaryEncoders.cs#L43
        string.Join(
            "\\\\\n",
            Enumerable
                .Range(0, array.Shape[0])
                .Select(
                    idxRow => string.Join(" & ",
                        Enumerable
                            .Range(0, array.Shape[1])
                            .Select(
                                idxCol => $"{array[idxRow, idxCol, 0]} + {array[idxRow, idxCol, 1]} i"
                            )
                    )
                )
        );

    internal static IEnumerable<NDArray> IterateOverLeftmostIndex(this NDArray array)
    {
        foreach (var idx in Enumerable.Range(0, array.shape[0]))
        {
            yield return array[idx, Slice.Ellipsis];
        }
    }

    internal static string AsTextMatrixOfComplex(this NDArray array, string rowSep = "\n") =>
        // NB: Assumes 𝑛 × 𝑛 × 2 array, where the trailing index is
        //     [real, imag].
        // TODO: Consolidate with logic at:
        //       https://github.com/microsoft/QuantumLibraries/blob/505fc27383c9914c3e1f60fb63d0acfe60b11956/Visualization/src/DisplayableUnitaryEncoders.cs#L43
        "[" + rowSep + string.Join(
            rowSep,
            Enumerable
                .Range(0, array.Shape[0])
                .Select(
                    idxRow => "[" + string.Join(", ",
                        Enumerable
                            .Range(0, array.Shape[1])
                            .Select(
                                idxCol => $"{array[idxRow, idxCol, 0]} + {array[idxRow, idxCol, 1]} i"
                            )
                    ) + "]"
                )
        ) + rowSep + "]";

    
    internal static IEnumerable<QsDeclarationAttribute> GetAttributesByName(
        this OperationInfo operation, string attributeName,
        string namespaceName = "Microsoft.Quantum.Documentation"
    ) =>
        operation.Header.Attributes.Where(
            attribute =>
                // Since QsNullable<UserDefinedType>.Item can be null,
                // we use a pattern match here to make sure that we have
                // an actual UDT to compare against.
                attribute.TypeId.Item is UserDefinedType udt &&
                udt.Namespace == namespaceName &&
                udt.Name == attributeName
        );

    internal static bool TryAsStringLiteral(this TypedExpression expression, [NotNullWhen(true)] out string? value)
    {
        if (expression.Expression is QsExpressionKind<TypedExpression, Identifier, ResolvedType>.StringLiteral literal)
        {
            value = literal.Item1;
            return true;
        }
        else
        {
            value = null;
            return false;
        }
    }
    internal static IEnumerable<string> GetStringAttributes(
        this OperationInfo operation, string attributeName,
        string namespaceName = "Microsoft.Quantum.Documentation"
    ) => operation
        .GetAttributesByName(attributeName, namespaceName)
        .Select(
            attribute =>
                attribute.Argument.TryAsStringLiteral(out var value)
                ? value : null
        )
        .Where(value => value != null)
        // The Where above ensures that all elements are non-nullable,
        // but the C# compiler doesn't quite figure that out, so we
        // need to help it with a no-op that uses the null-forgiving
        // operator.
        .Select(value => value!);

    internal static IDictionary<string?, string?> GetDictionaryAttributes(
        this OperationInfo operation, string attributeName,
        string namespaceName = "Microsoft.Quantum.Documentation"
    ) => operation
        .GetAttributesByName(attributeName, namespaceName)
        .SelectMany(
            attribute => attribute.Argument.Expression switch
            {
                QsExpressionKind<TypedExpression, Identifier, ResolvedType>.ValueTuple tuple =>
                    tuple.Item.Length != 2
                    ? throw new System.Exception("Expected attribute to be a tuple of two strings.")
                    : ImmutableList.Create((tuple.Item[0], tuple.Item[1])),
                _ => ImmutableList<(TypedExpression, TypedExpression)>.Empty
            }
        )
        .ToDictionary(
            attribute => attribute.Item1.TryAsStringLiteral(out var value) ? value : null,
            attribute => attribute.Item2.TryAsStringLiteral(out var value) ? value : null
        );

    internal static ICommsRouter? GetCommsRouter(this IChannel channel) =>
        // Workaround for https://github.com/microsoft/jupyter-core/issues/80.
        channel switch
        {
            { CommsRouter: {} router } => router,
            ChannelWithNewLines channelWithNewLines => channelWithNewLines.BaseChannel.GetCommsRouter(),
            _ => null
        };

    // Displays to channel and raises as an exception if applicable.
    public static void Report(this CommonMessages.UserMessage msg, IChannel channel, IConfigurationSource config)
    {
        var fullMsg = msg.Text;
        if (msg.Hint is {} hint && config.ShowHintsOnErrors)
        {
            fullMsg += "\nHint: " + hint;
        }
        channel.Stderr(fullMsg);
    }
}
