using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus.Commands.Converters;
using DSharpPlus.Commands.EventArgs;
using DSharpPlus.Commands.Exceptions;
using DSharpPlus.Commands.Trees;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSharpPlus.Commands.Processors;

public abstract class BaseCommandProcessor<TEventArgs, TConverter, TConverterContext, TCommandContext> : ICommandProcessor<TEventArgs>
    where TEventArgs : DiscordEventArgs
    where TConverter : IArgumentConverter
    where TConverterContext : ConverterContext
    where TCommandContext : CommandContext
{
    protected class LazyConverter
    {
        public required Type ParameterType { get; init; }

        public ConverterDelegate<TEventArgs>? ConverterDelegate { get; set; }
        public TConverter? ConverterInstance { get; set; }
        public Type? ConverterType { get; set; }

        public ConverterDelegate<TEventArgs> GetConverterDelegate(BaseCommandProcessor<TEventArgs, TConverter, TConverterContext, TCommandContext> processor, IServiceProvider serviceProvider)
        {
            if (ConverterDelegate is not null)
            {
                return ConverterDelegate;
            }

            ConverterInstance ??= GetConverter(serviceProvider);

            MethodInfo executeConvertAsyncMethod = processor.GetType().GetMethod(nameof(ExecuteConverterAsync), BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"Method {nameof(ExecuteConverterAsync)} does not exist");
            MethodInfo genericExecuteConvertAsyncMethod = executeConvertAsyncMethod.MakeGenericMethod(ParameterType) ?? throw new InvalidOperationException($"Method {nameof(ExecuteConverterAsync)} does not exist");
            return ConverterDelegate = (ConverterContext converterContext, TEventArgs eventArgs) => (Task<IOptional>)genericExecuteConvertAsyncMethod.Invoke(processor, [ConverterInstance, converterContext, eventArgs])!;
        }

        public TConverter GetConverter(IServiceProvider serviceProvider)
        {
            if (ConverterInstance is not null)
            {
                return ConverterInstance;
            }
            else if (ConverterType is null)
            {
                if (ConverterDelegate is null)
                {
                    throw new InvalidOperationException("No delegate, converter object, or converter type was provided.");
                }

                ConverterType = ConverterDelegate.Method.DeclaringType ?? throw new InvalidOperationException("No converter type was provided and the delegate's declaring type is null.");
            }

            if (!ConverterType.IsAssignableTo(typeof(TConverter)))
            {
                throw new InvalidOperationException($"Type {ConverterType.FullName} does not implement {typeof(TConverter).FullName}");
            }

            // Check if the type implements IArgumentConverter<TConverterContext, TEventArgs, T>
            Type genericArgumentConverter = ConverterType
                .GetInterfaces()
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IArgumentConverter<,,>))
                ?? throw new InvalidOperationException($"Type {ConverterType.FullName} does not implement {typeof(IArgumentConverter<,,>).FullName}");

            return (TConverter)ActivatorUtilities.CreateInstance(serviceProvider, ConverterType);
        }

        [SuppressMessage("Roslyn", "IDE0046", Justification = "Ternary rabbit hole.")]
        public override string? ToString()
        {
            if (ConverterDelegate is not null)
            {
                return ConverterDelegate.ToString();
            }
            else if (ConverterInstance is not null)
            {
                return ConverterInstance.ToString();
            }
            else if (ConverterType is not null)
            {
                return ConverterType.ToString();
            }
            else
            {
                return "<Empty Lazy Converter>";
            }
        }

        public override bool Equals(object? obj) => obj is LazyConverter lazyConverter && Equals(lazyConverter);
        public bool Equals(LazyConverter obj)
        {
            if (ParameterType != obj.ParameterType)
            {
                return false;
            }
            else if (ConverterDelegate is not null && obj.ConverterDelegate is not null)
            {
                return ConverterDelegate.Equals(obj.ConverterDelegate);
            }
            else if (ConverterInstance is not null && obj.ConverterInstance is not null)
            {
                return ConverterInstance.Equals(obj.ConverterInstance);
            }
            else if (ConverterType is not null && obj.ConverterType is not null)
            {
                return ConverterType.Equals(obj.ConverterType);
            }

            return false;
        }

        public override int GetHashCode() => HashCode.Combine(ParameterType, ConverterDelegate, ConverterInstance, ConverterType);
    }

    public IReadOnlyDictionary<Type, TConverter> Converters { get; protected set; } = new Dictionary<Type, TConverter>();
    public IReadOnlyDictionary<Type, ConverterDelegate<TEventArgs>> ConverterDelegates { get; protected set; } = new Dictionary<Type, ConverterDelegate<TEventArgs>>();
    // Redirect the interface to use the converter delegates property instead of the converters property
    IReadOnlyDictionary<Type, ConverterDelegate<TEventArgs>> ICommandProcessor<TEventArgs>.Converters => ConverterDelegates;

    protected readonly Dictionary<Type, LazyConverter> _lazyConverters = [];
    protected CommandsExtension? _extension;
    protected ILogger<BaseCommandProcessor<TEventArgs, TConverter, TConverterContext, TCommandContext>> _logger = NullLogger<BaseCommandProcessor<TEventArgs, TConverter, TConverterContext, TCommandContext>>.Instance;

    private static readonly Action<ILogger, string, Exception?> FailedConverterCreation = LoggerMessage.Define<string>(LogLevel.Error, new EventId(1), "Failed to create instance of converter '{FullName}' due to a lack of empty public constructors, lack of a service provider, or lack of services within the service provider.");

    public virtual void AddConverter<T>(TConverter converter) => AddConverter(typeof(T), converter);
    public virtual void AddConverter(Type type, TConverter converter) => AddConverter(new() { ParameterType = type, ConverterInstance = converter });
    public virtual void AddConverters(Assembly assembly) => AddConverters(assembly.GetTypes());
    public virtual void AddConverters(IEnumerable<Type> types)
    {
        foreach (Type type in types)
        {
            // Ignore types that don't have a concrete implementation (abstract classes or interfaces)
            // Additionally ignore types that have open generics (IArgumentConverter<TEventArgs, T>) instead of closed generics (IArgumentConverter<string>)
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition || !type.IsAssignableTo(typeof(TConverter)))
            {
                continue;
            }

            // Check if the type implements IArgumentConverter<TEventArgs, T>
            Type? genericArgumentConverter = type.GetInterfaces().FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IArgumentConverter<,,>));
            if (genericArgumentConverter is null)
            {
                BaseCommandLogging.InvalidArgumentConverterImplementation(_logger, type.FullName ?? type.Name, typeof(IArgumentConverter<,,>).FullName ?? typeof(IArgumentConverter<,,>).Name, null);
                continue;
            }

            // GenericTypeArguments[2] here is the T in IArgumentConverter<TConverterContext, TEventArgs, T>
            AddConverter(new() { ParameterType = genericArgumentConverter.GenericTypeArguments[2], ConverterType = type });
        }
    }

    protected void AddConverter(LazyConverter lazyConverter)
    {
        if (!_lazyConverters.TryAdd(lazyConverter.ParameterType, lazyConverter))
        {
            LazyConverter existingLazyConverter = _lazyConverters[lazyConverter.ParameterType];
            if (!lazyConverter.Equals(existingLazyConverter))
            {
                BaseCommandLogging.DuplicateArgumentConvertersRegistered(_logger, lazyConverter.ToString()!, existingLazyConverter.ParameterType.FullName ?? existingLazyConverter.ParameterType.Name, existingLazyConverter.ToString()!, null);
            }
        }
    }

    [MemberNotNull(nameof(_extension))]
    public virtual ValueTask ConfigureAsync(CommandsExtension extension)
    {
        _extension = extension;
        _logger = extension.ServiceProvider.GetService<ILogger<BaseCommandProcessor<TEventArgs, TConverter, TConverterContext, TCommandContext>>>() ?? NullLogger<BaseCommandProcessor<TEventArgs, TConverter, TConverterContext, TCommandContext>>.Instance;

        Type thisType = GetType();
        MethodInfo executeConvertAsyncMethod = thisType.GetMethod(nameof(ExecuteConverterAsync), BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"Method {nameof(ExecuteConverterAsync)} does not exist");
        AddConverters(thisType.Assembly);

        Dictionary<Type, TConverter> converters = [];
        Dictionary<Type, ConverterDelegate<TEventArgs>> converterDelegates = [];
        foreach (LazyConverter lazyConverter in _lazyConverters.Values)
        {
            converterDelegates.Add(lazyConverter.ParameterType, lazyConverter.GetConverterDelegate(this, extension.ServiceProvider));
            converters.Add(lazyConverter.ParameterType, lazyConverter.GetConverter(extension.ServiceProvider));
        }

        Converters = converters.ToFrozenDictionary();
        ConverterDelegates = converterDelegates.ToFrozenDictionary();
        return default;
    }

    public virtual async ValueTask<TCommandContext?> ParseArgumentsAsync(TConverterContext converterContext, TEventArgs eventArgs)
    {
        if (_extension is null)
        {
            return null;
        }

        Dictionary<CommandParameter, object?> parsedArguments = [];
        try
        {
            while (converterContext.NextParameter())
            {
                IOptional optional = await ConverterDelegates[GetConverterFriendlyBaseType(converterContext.Parameter.Type)](converterContext, eventArgs);
                if (!optional.HasValue)
                {
                    await _extension._commandErrored.InvokeAsync(converterContext.Extension, new CommandErroredEventArgs()
                    {
                        Context = CreateCommandContext(converterContext, eventArgs, parsedArguments),
                        Exception = new ArgumentParseException(converterContext.Parameter, null, $"Argument Converter for type {converterContext.Parameter.Type.FullName} was unable to parse the argument."),
                        CommandObject = null
                    });

                    return null;
                }

                parsedArguments.Add(converterContext.Parameter, optional.RawValue);
            }

            if (parsedArguments.Count != converterContext.Command.Parameters.Count)
            {
                // Try to fill with default values
                foreach (CommandParameter parameter in converterContext.Command.Parameters.Skip(parsedArguments.Count))
                {
                    if (!parameter.DefaultValue.HasValue)
                    {
                        await _extension._commandErrored.InvokeAsync(converterContext.Extension, new CommandErroredEventArgs()
                        {
                            Context = CreateCommandContext(converterContext, eventArgs, parsedArguments),
                            Exception = new ArgumentParseException(converterContext.Parameter, null, "No value was provided for this parameter."),
                            CommandObject = null
                        });

                        return null;
                    }

                    parsedArguments.Add(parameter, parameter.DefaultValue.Value);
                }
            }
        }
        catch (Exception error)
        {
            await _extension._commandErrored.InvokeAsync(converterContext.Extension, new CommandErroredEventArgs()
            {
                Context = CreateCommandContext(converterContext, eventArgs, parsedArguments),
                Exception = new ArgumentParseException(converterContext.Parameter, error),
                CommandObject = null
            });

            return null;
        }

        return CreateCommandContext(converterContext, eventArgs, parsedArguments);
    }

    public abstract TCommandContext CreateCommandContext(TConverterContext converterContext, TEventArgs eventArgs, Dictionary<CommandParameter, object?> parsedArguments);

    protected virtual Type GetConverterFriendlyBaseType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type, nameof(type));

        if (type.IsEnum)
        {
            return typeof(Enum);
        }
        else if (type.IsArray)
        {
            return type.GetElementType()!;
        }

        return Nullable.GetUnderlyingType(type) ?? type;
    }

    protected virtual async Task<IOptional> ExecuteConverterAsync<T>(TConverter converter, TConverterContext converterContext, TEventArgs eventArgs)
    {
        IArgumentConverter<TConverterContext, TEventArgs, T> strongConverter = (IArgumentConverter<TConverterContext, TEventArgs, T>)converter;
        if (!converterContext.NextArgument())
        {
            return converterContext.Parameter.DefaultValue.HasValue
                ? Optional.FromValue(converterContext.Parameter.DefaultValue.Value)
                : throw new ArgumentParseException(converterContext.Parameter, message: $"Missing argument for {converterContext.Parameter.Name}.");
        }
        else if (!converterContext.Parameter.Attributes.OfType<ParamArrayAttribute>().Any())
        {
            return await strongConverter.ConvertAsync(converterContext, eventArgs);
        }

        List<T> values = [];
        do
        {
            Optional<T> optional = await strongConverter.ConvertAsync(converterContext, eventArgs);
            if (!optional.HasValue)
            {
                break;
            }

            values.Add(optional.Value);
        } while (converterContext.NextArgument());
        return Optional.FromValue(values.ToArray());
    }
}
