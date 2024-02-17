// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Bicep resource.
/// </summary>
/// <param name="name">Name of the resource. This will be the name of the deployment.</param>
/// <param name="templateFile">The path to the bicep file.</param>
/// <param name="templateString">A bicep snippet.</param>
/// <param name="templateResouceName">The name of an embedded resource that represents the bicep file.</param>
public class AzureBicepResource(string name, string? templateFile = null, string? templateString = null, string? templateResouceName = null) :
    Resource(name),
    IAzureResource
{
    internal string? TemplateFile { get; } = templateFile;

    internal string? TemplateString { get; } = templateString;

    internal string? TemplateResourceName { get; } = templateResouceName;

    /// <summary>
    /// Parameters that will be passed into the bicep template.
    /// </summary>
    public Dictionary<string, object?> Parameters { get; } = [];

    /// <summary>
    /// Outputs that will be generated by the bicep template.
    /// </summary>
    public Dictionary<string, string?> Outputs { get; } = [];

    /// <summary>
    /// Secret outputs that will be generated by the bicep template.
    /// </summary>
    public Dictionary<string, string?> SecretOutputs { get; } = [];

    /// <summary>
    /// Gets the path to the bicep file. If the template is a string or embedded resource, it will be written to a temporary file.
    /// </summary>
    /// <param name="directory">The directory where the bicep file will be written to (if it's a temporary file)</param>
    /// <param name="deleteTemporaryFileOnDispose">A boolean that determines if the file should be deleted on disposal of the <see cref="BicepTemplateFile"/>.</param>
    /// <returns>A <see cref="BicepTemplateFile"/> that represents the bicep file.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public BicepTemplateFile GetBicepTemplateFile(string? directory = null, bool deleteTemporaryFileOnDispose = true)
    {
        // Throw if multiple template sources are specified
        if (TemplateFile is not null && (TemplateString is not null || TemplateResourceName is not null))
        {
            throw new InvalidOperationException("Multiple template sources are specified.");
        }

        var path = TemplateFile;
        var isTempFile = false;

        if (path is null)
        {
            isTempFile = directory is null;

            path = Path.GetTempFileName() + ".bicep";

            if (TemplateResourceName is null)
            {
                // REVIEW: Consider making users specify a name for the template
                File.WriteAllText(path, TemplateString);
            }
            else
            {
                path = directory is null
                    ? path
                    : Path.Combine(directory, $"{TemplateResourceName.ToLowerInvariant()}");

                // REVIEW: We should allow the user to specify the assembly where the resources reside.
                using var resourceStream = GetType().Assembly.GetManifestResourceStream(TemplateResourceName)
                    ?? throw new InvalidOperationException($"Could not find resource {TemplateResourceName} in assembly {GetType().Assembly}");

                using var fs = File.OpenWrite(path);
                resourceStream.CopyTo(fs);
            }
        }

        return new(path, isTempFile && deleteTemporaryFileOnDispose);
    }

    // TODO: Make the name bicep safe
    /// <summary>
    /// TODO: Doc Comments
    /// </summary>
    /// <returns></returns>
    public string CreateBicepResourceName() => Name.ToLower();

    private static string EvalParameter(object? input)
    {
        static string Quote(string s) => $"\"{s}\"";
        static string SingleQuote(string s) => $"'{s}'";
        static string Parenthesize(string s) => $"[{s}]";
        static string Join(IEnumerable<string> s) => string.Join(", ", s);

        return input switch
        {
            string s => Quote(s),
            IEnumerable<string> enumerable => Quote(Parenthesize(Join(enumerable.Select(SingleQuote)))),
            IResourceBuilder<IResourceWithConnectionString> builder => Quote(builder.Resource.GetConnectionString() ?? throw new InvalidOperationException("Missing connection string")),
            IResourceBuilder<ParameterResource> p => Quote(p.Resource.Value),
            // REVIEW: The value might not be calculated yet
            BicepOutputReference output => Quote(output.Name + "=" + output.Resource.GetChecksum()),
            object o => Quote(input.ToString()!),
            null => ""
        };
    }

    // TODO: Use this when caching the results
    /// <summary>
    /// TODO: Doc Comments
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public string GetChecksum()
    {
        // TODO: PERF Inefficient

        // First the parameters
        var combined = string.Join(";", Parameters.OrderBy(p => p.Key).Select(p => $"{p.Key}={EvalParameter(p.Value)}"));

        if (TemplateFile is not null)
        {
            combined += File.ReadAllText(TemplateFile);
        }
        else if (TemplateString is not null)
        {
            combined += TemplateString;
        }
        else if (TemplateResourceName is not null)
        {
            using var stream = GetType().Assembly.GetManifestResourceStream(TemplateResourceName) ??
                throw new InvalidOperationException($"Could not find resource {TemplateResourceName} in assembly {GetType().Assembly}");

            combined += new StreamReader(stream).ReadToEnd();
        }

        var hashedContents = Crc32.Hash(Encoding.UTF8.GetBytes(combined));

        return Convert.ToHexString(hashedContents).ToLowerInvariant();
    }

    /// <summary>
    /// Writes the resource to the manifest.
    /// </summary>
    /// <param name="context">The <see cref="ManifestPublishingContext"/>.</param>
    public virtual void WriteToManifest(ManifestPublishingContext context)
    {
        context.Writer.WriteString("type", "azure.bicep.v0");

        using var template = GetBicepTemplateFile(Path.GetDirectoryName(context.ManifestPath), deleteTemporaryFileOnDispose: false);
        var path = template.Path;

        // REVIEW: This should be in the ManifestPublisher
        if (this is IResourceWithConnectionString c && c.ConnectionStringExpression is string connectionString)
        {
            context.Writer.WriteString("connectionString", connectionString);
        }

        // REVIEW: Consider multiple files.
        context.Writer.WriteString("path", context.GetManifestRelativePath(path));

        if (Parameters.Count > 0)
        {
            context.Writer.WriteStartObject("params");
            foreach (var input in Parameters)
            {
                if (input.Value is JsonNode || input.Value is IEnumerable<string>)
                {
                    context.Writer.WritePropertyName(input.Key);
                    // Write JSON objects to the manifest for JSON node parameters
                    JsonSerializer.Serialize(context.Writer, input.Value);
                    continue;
                }

                var value = input.Value switch
                {
                    IResourceBuilder<ParameterResource> p => p.Resource.ValueExpression,
                    IResourceBuilder<IResourceWithConnectionString> p => p.Resource.ConnectionStringReferenceExpression,
                    BicepOutputReference output => output.ValueExpression,
                    object obj => obj.ToString(),
                    null => ""
                };

                context.Writer.WriteString(input.Key, value);
            }
            context.Writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Known parameters that can be used in the bicep template.
    /// </summary>
    public static class KnownParameters
    {
        /// <summary>
        /// TODO: Doc Comments
        /// </summary>
        public const string PrincipalId = "principalId";
        /// <summary>
        /// TODO: Doc Comments
        /// </summary>
        public const string PrincipalName = "principalName";
        /// <summary>
        /// TODO: Doc Comments
        /// </summary>
        public const string PrincipalType = "principalType";
        /// <summary>
        /// TODO: Doc Comments
        /// </summary>
        public static string KeyVaultName = "keyVaultName";
    }
}

/// <summary>
/// Represents a bicep template file.
/// </summary>
/// <param name="path">The path to the bicep file.</param>
/// <param name="deleteFileOnDispose">Determines if the file should be deleted on disposal.</param>
public readonly struct BicepTemplateFile(string path, bool deleteFileOnDispose) : IDisposable
{
    /// <summary>
    /// The path to the bicep file.
    /// </summary>
    public string Path { get; } = path;

    /// <summary>
    /// Releases the resources used by the current instance of <see cref="BicepTemplateFile" />.
    /// </summary>
    public void Dispose()
    {
        if (deleteFileOnDispose)
        {
            File.Delete(Path);
        }
    }
}

/// <summary>
/// A reference to an output from a bicep template.
/// </summary>
/// <param name="name">The name of the output</param>
/// <param name="resource">The <see cref="AzureBicepResource"/>.</param>
public class BicepOutputReference(string name, AzureBicepResource resource)
{
    /// <summary>
    /// Name of the output.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// The instance of the bicep resource.
    /// </summary>
    public AzureBicepResource Resource { get; } = resource;

    /// <summary>
    /// The value of the output.
    /// </summary>
    public string? Value
    {
        get
        {
            if (!Resource.Outputs.TryGetValue(Name, out var value))
            {
                throw new InvalidOperationException($"No output for {Name}");
            }

            return value;
        }
    }

    /// <summary>
    /// The expression used in the manifest to reference the value of the output.
    /// </summary>
    public string ValueExpression => $"{{{Resource.Name}.outputs.{Name}}}";
}

/// <summary>
/// Extension methods for adding Azure Bicep resources to the application model.
/// </summary>
public static class AzureBicepTemplateResourceExtensions
{
    /// <summary>
    /// Adds an Azure Bicep resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the deployment name.</param>
    /// <param name="bicepFile">The path to the bicep file on disk. This path is relative to the apphost's project directory.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureBicepResource> AddBicepTemplate(this IDistributedApplicationBuilder builder, string name, string bicepFile)
    {
        var path = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, bicepFile));
        var resource = new AzureBicepResource(name, templateFile: path, templateString: null);
        return builder.AddResource(resource)
                      .WithManifestPublishingCallback(resource.WriteToManifest);
    }

    /// <summary>
    /// Adds an Azure Bicep resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the deployment name.</param>
    /// <param name="bicepContent">A string that represents a snippet of bicep.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<AzureBicepResource> AddBicepTemplateString(this IDistributedApplicationBuilder builder, string name, string bicepContent)
    {
        var resource = new AzureBicepResource(name, templateFile: null, templateString: bicepContent);
        return builder.AddResource(resource)
                      .WithManifestPublishingCallback(resource.WriteToManifest);
    }

    /// <summary>
    /// Gets a reference to a  output from a bicep template.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">Name of the output.</param>
    /// <returns>A <see cref="BicepOutputReference"/> that represents the output.</returns>
    public static BicepOutputReference GetOutput(this IResourceBuilder<AzureBicepResource> builder, string name)
    {
        return new BicepOutputReference(name, builder.Resource);
    }

    /// <summary>
    /// Adds an environment variable to the resource with the value of the output from the bicep template.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="bicepOutputReference">The reference to the bicep output.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithEnvironment<T>(this IResourceBuilder<T> builder, string name, BicepOutputReference bicepOutputReference)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(ctx =>
        {
            if (ctx.ExecutionContext.Operation == DistributedApplicationOperation.Publish)
            {
                ctx.EnvironmentVariables[name] = bicepOutputReference.ValueExpression;
                return;
            }

            ctx.EnvironmentVariables[name] = bicepOutputReference.Value!;
        });
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/>.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[name] = null;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, string value)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, IEnumerable<string> value)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, JsonNode value)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<ParameterResource> value)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, IResourceBuilder<IResourceWithConnectionString> value)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds a parameter to the bicep template.
    /// </summary>
    /// <typeparam name="T">The <see cref="AzureBicepResource"/></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="value">The value of the parameter.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> WithParameter<T>(this IResourceBuilder<T> builder, string name, BicepOutputReference value)
        where T : AzureBicepResource
    {
        builder.Resource.Parameters[name] = value;
        return builder;
    }
}
