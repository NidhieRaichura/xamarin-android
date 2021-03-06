﻿using System;
using System.Linq;
using Mono.Cecil;

using Java.Interop.Tools.Cecil;

using Mono.Linker.Steps;
using Mono.Tuner;

namespace MonoDroid.Tuner
{
	class MonoDroidMarkStep : MarkStep
	{
		const string ICustomMarshalerName = "System.Runtime.InteropServices.ICustomMarshaler";

		// If this is one of our infrastructure methods that has [Register], like:
		// [Register ("hasWindowFocus", "()Z", "GetHasWindowFocusHandler")],
		// we need to preserve the "GetHasWindowFocusHandler" method as well.
		protected override void DoAdditionalMethodProcessing (MethodDefinition method)
		{
			string member;

			if (!method.TryGetRegisterMember (out member))
				return;

			PreserveRegisteredMethod (method.DeclaringType, member);
		}

		protected override void DoAdditionalTypeProcessing (TypeDefinition type)
		{
			// If we are preserving a Mono.Android interface,
			// preserve all members on the interface.
			if (!type.IsInterface)
				return;

			// Mono.Android interfaces will always inherit IJavaObject
			if (!type.ImplementsIJavaObject ())
				return;

			foreach (MethodReference method in type.Methods)
				MarkMethod (method);
		}
		
		private void PreserveRegisteredMethod (TypeDefinition type, string member)
		{
			var type_ptr = type;
			var pos = member.IndexOf (':');

			if (pos > 0) {
				var type_name = member.Substring (pos + 1);
				member = member.Substring (0, pos);
				type_ptr = type_ptr.Module.Types.FirstOrDefault (t => t.FullName == type_name);
			}

			if (type_ptr == null)
				return;

			while (MarkNamedMethod (type, member) == 0 && type.BaseType != null)
				type = type.BaseType.Resolve ();
		}

		protected override TypeDefinition MarkType (TypeReference reference)
		{
			TypeDefinition type = base.MarkType (reference);
			if (type == null)
				return null;

			switch (type.Module.Assembly.Name.Name) {
			case "mscorlib":
				ProcessCorlib (type);
				break;
			case "System.Core":
				ProcessSystemCore (type);
				break;
			case "System.Data":
				ProcessSystemData (type);
				break;
			case "System":
				ProcessSystem (type);
				break;
			}

			if (type.HasMethods && type.HasInterfaces && type.Implements (ICustomMarshalerName)) {
				foreach (MethodDefinition method in type.Methods) {
					if (method.Name == "GetInstance" && method.IsStatic && method.HasParameters && method.Parameters.Count == 1 && method.ReturnType.FullName == ICustomMarshalerName && method.Parameters.First ().ParameterType.FullName == "System.String") {
						MarkMethod (method);
						break;
					}
				}
			}

			return type;
		}

		bool DebugBuild {
			get { return _context.LinkSymbols; }
		}

		void ProcessCorlib (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Runtime.CompilerServices":
				switch (type.Name) {
				case "AsyncTaskMethodBuilder":
					if (DebugBuild) {
						MarkNamedMethod (type, "SetNotificationForWaitCompletion");
						MarkNamedMethod (type, "get_ObjectIdForDebugger");
					}
					break;
				case "AsyncTaskMethodBuilder`1":
					if (DebugBuild) {
						MarkNamedMethod (type, "SetNotificationForWaitCompletion");
						MarkNamedMethod (type, "get_ObjectIdForDebugger");
					}
					break;
				}
				break;
			case "System.Threading.Tasks":
				switch (type.Name) {
				case "Task":
					if (DebugBuild)
						MarkNamedMethod (type, "NotifyDebuggerOfWaitCompletion");
					break;
				}
				break;
			}
		}

		void ProcessSystemCore (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Linq.Expressions":
				switch (type.Name) {
				case "LambdaExpression":
					var expr_t = type.Module.GetType ("System.Linq.Expressions.Expression`1");
					if (expr_t != null)
						MarkNamedMethod (expr_t, "Create");
					break;
				}
				break;
			case "System.Linq.Expressions.Compiler":
				switch (type.Name) {
				case "LambdaCompiler":
					MarkNamedMethod (type.Module.GetType ("System.Runtime.CompilerServices.RuntimeOps"), "Quote");
					break;
				}
				break;
			}
		}

		protected AssemblyDefinition GetAssembly (string assemblyName)
		{
			AssemblyDefinition ad;
			_context.TryGetLinkedAssembly (assemblyName, out ad);
			return ad;
		}

		protected TypeDefinition GetType (string assemblyName, string typeName)
		{
			AssemblyDefinition ad = GetAssembly (assemblyName);
			return ad == null ? null : GetType (ad, typeName);
		}

		protected TypeDefinition GetType (AssemblyDefinition assembly, string typeName)
		{
			return assembly.MainModule.GetType (typeName);
		}

		void ProcessSystemData (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Data.SqlTypes":
				switch (type.Name) {
				case "SqlXml":
					// TODO: Needed only if CreateSqlReaderDelegate is used
					TypeDefinition xml_reader = GetType ("System.Xml", "System.Xml.XmlReader");
					MarkNamedMethod (xml_reader, "CreateSqlReader");
					break;
				}
				break;
			}
		}

		void ProcessSystem (TypeDefinition type)
		{
			switch (type.Namespace) {
			case "System.Diagnostics":
				switch (type.Name) {
				// see mono/metadata/process.c
				case "FileVersionInfo":
				case "ProcessModule":
					// fields are initialized by the runtime, if the type is here then all (instance) fields must be present
					MarkFields (type, false);
					break;
				}
				break;
			case "System.Net.Sockets":
				switch (type.Name) {
				case "IPAddress":
					// mono/metadata/socket-io.c directly access 'm_Address' and 'm_Numbers'
					MarkFields (type, false);
					break;
				case "IPv6MulticastOption":
					// mono/metadata/socket-io.c directly access 'group' and 'ifIndex' private instance fields
					MarkFields (type, false);
					break;
				case "LingerOption":
					// mono/metadata/socket-io.c directly access 'enabled' and 'seconds' private instance fields
					MarkFields (type, false);
					break;
				case "MulticastOption":
					// mono/metadata/socket-io.c directly access 'group' and 'local' private instance fields
					MarkFields (type, false);
					break;
				case "Socket":
					// mono/metadata/socket-io.c directly access 'ipv4Supported', 'ipv6Supported' (static) and 'socket' (instance)
					MarkFields (type, true);
					break;
				case "SocketAddress":
					// mono/metadata/socket-io.c directly access 'data'
					MarkFields (type, false);
					break;
				}
				break;
			case "":
				if (!type.IsNested)
					break;

				switch (type.Name) {
				case "SocketAsyncResult":
					// mono/metadata/socket-io.h defines this structure (MonoSocketAsyncResult) for the runtime usage
					MarkFields (type, false);
					break;
				case "ProcessAsyncReader":
					// mono/metadata/socket-io.h defines this structure (MonoSocketAsyncResult) for the runtime usage
					MarkFields (type, false);
					break;
				}
				break;
			}
		}
	}
}
