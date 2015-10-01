﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace System.CommandLine
{
    public sealed partial class ArgumentSyntax
    {
        private readonly IEnumerable<string> _arguments;
        private readonly List<ArgumentCommand> _commands = new List<ArgumentCommand>();
        private readonly List<Argument> _options = new List<Argument>();
        private readonly List<Argument> _parameters = new List<Argument>();

        private ArgumentParser _parser;
        private ArgumentCommand _definedCommand;
        private ArgumentCommand _activeCommand;

        internal ArgumentSyntax(IEnumerable<string> arguments)
        {
            _arguments = arguments;

            ApplicationName = GetApplicationName();
            HandleErrors = true;
            HandleHelp = true;
            HandleResponseFiles = true;
        }

        public static ArgumentSyntax Parse(IEnumerable<string> arguments, Action<ArgumentSyntax> defineAction)
        {
            if (arguments == null)
                throw new ArgumentNullException("arguments");

            if (defineAction == null)
                throw new ArgumentNullException("defineAction");

            var syntax = new ArgumentSyntax(arguments);
            defineAction(syntax);
            syntax.Validate();
            return syntax;
        }

        private void Validate()
        {
            // Check whether help is requested

            if (HandleHelp && IsHelpRequested())
            {
                var helpText = GetHelpText();
                Console.Error.Write(helpText);

                // TODO: This should use Environment.Exit(0) but this API isn't available yet.
#if NET_FX
                Environment.Exit(0);
#else
                Environment.FailFast(string.Empty);
#endif
            }

            // Check for invalid or missing command

            if (_activeCommand == null && _commands.Any())
            {
                var unreadCommand = Parser.GetUnreadCommand();
                var message = unreadCommand == null
                    ? Strings.MissingCommand
                    : string.Format(Strings.UnknownCommandFmt, unreadCommand);
                ReportError(message);
            }

            // Check for invalid options and extra parameters

            foreach (var option in Parser.GetUnreadOptionNames())
            {
                var message = string.Format(Strings.InvalidOptionFmt, option);
                ReportError(message);
            }

            foreach (var parameter in Parser.GetUnreadParameters())
            {
                var message = string.Format(Strings.ExtraParameterFmt, parameter);
                ReportError(message);
            }
        }

        private bool IsHelpRequested()
        {
            return Parser.GetUnreadOptionNames()
                         .Any(a => string.Equals(a, @"-?", StringComparison.Ordinal) ||
                                   string.Equals(a, @"-h", StringComparison.Ordinal) ||
                                   string.Equals(a, @"--help", StringComparison.Ordinal));
        }

        public void ReportError(string message)
        {
            if (HandleErrors)
            {
                Console.Error.WriteLine(Strings.ErrorWithMessageFmt, message);

                // TODO: This should use Environment.Exit(1) but this API isn't available yet.
#if NET_FX
                Environment.Exit(1);
#else
                Environment.FailFast(string.Empty);
#endif
            }

            throw new ArgumentSyntaxException(message);
        }

        public ArgumentCommand<T> DefineCommand<T>(string name, T value)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(Strings.NameMissing, "name");

            if (!IsValidName(name))
            {
                var message = string.Format(Strings.CommandNameIsNotValidFmt, name);
                throw new ArgumentException(message, "name");
            }

            if (_commands.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
            {
                var message = string.Format(Strings.CommandAlreadyDefinedFmt, name);
                throw new InvalidOperationException(message);
            }

            if (_parameters.Any(c => c.Command == null))
                throw new InvalidOperationException(Strings.CannotDefineCommandsIfGlobalParametersExist);

            var definedCommand = new ArgumentCommand<T>(name, value);
            _commands.Add(definedCommand);
            _definedCommand = definedCommand;

            if (_activeCommand != null)
                return definedCommand;

            if (!Parser.TryParseCommand(name))
                return definedCommand;

            _activeCommand = _definedCommand;
            _activeCommand.MarkActive();

            return definedCommand;
        }

        public Argument<T> DefineOption<T>(string name, T defaultValue, Func<string, T> valueConverter)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(Strings.NameMissing, "name");

            if (DefinedParameters.Any())
                throw new InvalidOperationException(Strings.OptionsMustBeDefinedBeforeParameters);

            var names = ParseOptionNameList(name);
            var option = new Argument<T>(_definedCommand, names, defaultValue);
            _options.Add(option);

            if (_activeCommand != _definedCommand)
                return option;

            try
            {
                T value;
                if (Parser.TryParseOption(option.GetDisplayName(), option.Names, valueConverter, out value))
                    option.SetValue(value);
            }
            catch (ArgumentSyntaxException ex)
            {
                ReportError(ex.Message);
            }

            return option;
        }

        public ArgumentList<T> DefineOptionList<T>(string name, IReadOnlyList<T> defaultValue, Func<string, T> valueConverter)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(Strings.NameMissing, "name");

            if (DefinedParameters.Any())
                throw new InvalidOperationException(Strings.OptionsMustBeDefinedBeforeParameters);

            var names = ParseOptionNameList(name);
            var optionList = new ArgumentList<T>(_definedCommand, names, defaultValue);
            _options.Add(optionList);

            if (_activeCommand != _definedCommand)
                return optionList;

            try
            {
                IReadOnlyList<T> value;
                if (Parser.TryParseOptionList(optionList.GetDisplayName(), optionList.Names, valueConverter, out value))
                    optionList.SetValue(value);
            }
            catch (ArgumentSyntaxException ex)
            {
                ReportError(ex.Message);
            }

            return optionList;
        }

        public Argument<T> DefineParameter<T>(string name, T defaultValue, Func<string, T> valueConverter)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(Strings.NameMissing, "name");

            if (!IsValidName(name))
            {
                var message = string.Format(Strings.ParameterNameIsNotValidFmt, name);
                throw new ArgumentException(message, "name");
            }

            if (DefinedParameters.Any(p => p.IsList))
                throw new InvalidOperationException(Strings.ParametersCannotBeDefinedAfterLists);

            if (DefinedParameters.Any(p => string.Equals(name, p.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var message = string.Format(Strings.ParameterAlreadyDefinedFmt, name);
                throw new InvalidOperationException(message);
            }

            var parameter = new Argument<T>(_definedCommand, name, defaultValue);
            _parameters.Add(parameter);

            if (_activeCommand != _definedCommand)
                return parameter;

            try
            {
                T value;
                if (Parser.TryParseParameter(parameter.GetDisplayName(), valueConverter, out value))
                    parameter.SetValue(value);
            }
            catch (ArgumentSyntaxException ex)
            {
                ReportError(ex.Message);
            }

            return parameter;
        }

        public ArgumentList<T> DefineParameterList<T>(string name, IReadOnlyList<T> defaultValue, Func<string, T> valueConverter)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(Strings.NameMissing, "name");

            if (!IsValidName(name))
            {
                var message = string.Format(Strings.ParameterNameIsNotValidFmt, name);
                throw new ArgumentException(message, "name");
            }

            if (DefinedParameters.Any(p => p.IsList))
                throw new InvalidOperationException(Strings.CannotDefineMultipleParameterLists);

            var parameterList = new ArgumentList<T>(_definedCommand, name, defaultValue);
            _parameters.Add(parameterList);

            if (_activeCommand != _definedCommand)
                return parameterList;

            try
            {
                IReadOnlyList<T> values;
                if (Parser.TryParseParameterList(parameterList.GetDisplayName(), valueConverter, out values))
                    parameterList.SetValue(values);
            }
            catch (ArgumentSyntaxException ex)
            {
                ReportError(ex.Message);
            }

            return parameterList;
        }

        private static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (name[0] == '-')
                return false;

            return name.All(c => char.IsLetterOrDigit(c) ||
                                 c == '-' ||
                                 c == '_');
        }

        private IEnumerable<string> ParseOptionNameList(string name)
        {
            var names = name.Split('|').Select(n => n.Trim()).ToArray();

            foreach (var alias in names)
            {
                if (!IsValidName(alias))
                {
                    var message = string.Format(Strings.OptionNameIsNotValidFmt, alias);
                    throw new ArgumentException(message, "name");
                }

                foreach (var option in DefinedOptions)
                {
                    if (option.Names.Any(n => string.Equals(n, alias, StringComparison.Ordinal)))
                    {
                        var message = string.Format(Strings.OptionAlreadyDefinedFmt, alias);
                        throw new InvalidOperationException(message);
                    }
                }
            }

            return names;
        }

        private IEnumerable<string> ParseResponseFile(string fileName)
        {
            if (!HandleResponseFiles)
                return null;

            if (!File.Exists(fileName))
            {
                var message = string.Format(Strings.ResponseFileDoesNotExistFmt, fileName);
                ReportError(message);
            }

            return File.ReadLines(fileName);
        }

        private static string GetApplicationName()
        {
            // TODO: This should use Environment.GetCommandLineArgs() but this API isn't available yet.
#if NET_FX
            var processPath = Environment.GetCommandLineArgs()[0];
            var processName = Path.GetFileNameWithoutExtension(processPath);
            return processName;
#else
            return string.Empty;
#endif
        }

        public string ApplicationName { get; set; }

        public bool HandleErrors { get; set; }

        public bool HandleHelp { get; set; }

        public bool HandleResponseFiles { get; set; }

        private ArgumentParser Parser
        {
            get
            {
                if (_parser == null)
                    _parser = new ArgumentParser(_arguments, ParseResponseFile);

                return _parser;
            }
        }

        private IEnumerable<Argument> DefinedOptions
        {
            get { return _options.Where(o => o.Command == null || o.Command == _definedCommand); }
        }

        private IEnumerable<Argument> DefinedParameters
        {
            get { return _parameters.Where(p => p.Command == null || p.Command == _definedCommand); }
        }

        public ArgumentCommand ActiveCommand
        {
            get { return _activeCommand; }
        }

        public IReadOnlyList<ArgumentCommand> Commands
        {
            get { return _commands; }
        }

        public IEnumerable<Argument> GetArguments()
        {
            return _options.Concat(_parameters);
        }

        public IEnumerable<Argument> GetArguments(ArgumentCommand command)
        {
            return GetArguments().Where(c => c.Command == null || c.Command == command);
        }

        public IEnumerable<Argument> GetOptions()
        {
            return _options;
        }

        public IEnumerable<Argument> GetOptions(ArgumentCommand command)
        {
            return _options.Where(c => c.Command == null || c.Command == command);
        }

        public IEnumerable<Argument> GetParameters()
        {
            return _parameters;
        }

        public IEnumerable<Argument> GetParameters(ArgumentCommand command)
        {
            return _parameters.Where(c => c.Command == null || c.Command == command);
        }

        public IEnumerable<Argument> GetActiveArguments()
        {
            return GetArguments(ActiveCommand);
        }

        public IEnumerable<Argument> GetActiveOptions()
        {
            return GetOptions(ActiveCommand);
        }

        public IEnumerable<Argument> GetActiveParameters()
        {
            return GetParameters(ActiveCommand);
        }

        public string GetHelpText()
        {
            // TODO: This should use Console.WindowWidth but this API isn't available yet.
            // return GetHelpText(Console.WindowWidth - 2);
            return GetHelpText(72);
        }

        public string GetHelpText(int maxWidth)
        {
            return HelpTextGenerator.Generate(this, maxWidth);
        }
    }
}