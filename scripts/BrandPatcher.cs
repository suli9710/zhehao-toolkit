using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Resources;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class ZhehaoBrandPatcher
{
    private const string Brand = "喆好工具箱";

    private static string ReplaceDisplayText(string value)
    {
        if (value == "主页、联系与版本") return "简介与版本";
        // These strings identify existing tasks, migrations or third party credits.
        if (value == "LaoYing Toolkit Startup" || value == "LaoYing Optimizer.lnk") return value;
        return value.Replace("LaoYing Toolkit", Brand).Replace("老嘤工具箱", Brand);
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    private static int ReplaceSameLengthBytes(byte[] bytes, string original, string replacement, bool retainCopyright)
    {
        byte[] oldBytes = Encoding.UTF8.GetBytes(original);
        byte[] newBytes = Encoding.UTF8.GetBytes(replacement);
        if (oldBytes.Length != newBytes.Length) throw new InvalidOperationException("BAML byte lengths differ.");
        int changed = 0;
        for (int at = 0; at <= bytes.Length - oldBytes.Length; at++)
        {
            bool matches = true;
            for (int index = 0; index < oldBytes.Length; index++)
                if (bytes[at + index] != oldBytes[index]) { matches = false; break; }
            if (!matches) continue;
            if (retainCopyright)
            {
                string preceding = Encoding.UTF8.GetString(bytes, Math.Max(0, at - 20), Math.Min(at, 20));
                if (preceding.Contains("© 2026")) continue;
            }
            Buffer.BlockCopy(newBytes, 0, bytes, at, newBytes.Length);
            changed++;
            at += oldBytes.Length - 1;
        }
        return changed;
    }

    private static MethodReference FindReferencedMethod(ModuleDefinition module, string declaringType, string name, params string[] parameters)
    {
        foreach (var type in AllTypes(module.Types))
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    var reference = instruction.Operand as MethodReference;
                    if (reference == null || reference.DeclaringType.FullName != declaringType ||
                        reference.Name != name || reference.Parameters.Count != parameters.Length) continue;
                    bool matches = true;
                    for (int index = 0; index < parameters.Length; index++)
                        if (reference.Parameters[index].ParameterType.FullName != parameters[index]) matches = false;
                    if (matches) return reference;
                }
            }
        throw new InvalidOperationException("Required original method reference missing: " + declaringType + "." + name);
    }

    private static void ExpandShortBranches(MethodDefinition method)
    {
        // Inserting a call can move an existing short branch beyond its signed-byte range.
        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
                case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
                case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
                case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
                case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
                case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
                case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
                case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
                case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
                case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
                case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
                case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
                case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
                case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
            }
        }
    }

    private static void EmbedPboExecutablePathResolution(ModuleDefinition module, List<string> changes)
    {
        const string prefix = "LaoYing.Optimizer.App.Modules.Pbo.";
        TypeDefinition owner = module.GetType(prefix + "WindowsPboToolboxBindingFiles");
        TypeDefinition protection = module.GetType(prefix + "PboToolboxBindingProtection");
        if (owner == null || protection == null) throw new InvalidOperationException("PBO binding types missing.");
        TypeDefinition native = null;
        foreach (var nested in owner.NestedTypes) if (nested.Name == "Native") native = nested;
        if (native == null) throw new InvalidOperationException("PBO native file API missing.");
        MethodDefinition createFile = null, getFinalPath = null, validate = null, rejectReparse = null;
        foreach (var method in native.Methods)
        {
            if (method.Name == "CreateFile") createFile = method;
            if (method.Name == "GetFinalPathNameByHandle") getFinalPath = method;
        }
        foreach (var method in owner.Methods) if (method.Name == "ValidateNode") validate = method;
        foreach (var method in protection.Methods) if (method.Name == "RejectReparsePoints") rejectReparse = method;
        if (createFile == null || getFinalPath == null || validate == null || rejectReparse == null)
            throw new InvalidOperationException("PBO file validation methods missing.");

        // Reuse the bundled program's own Windows imports and framework references.
        // Only the executable's input path is resolved; protected records retain their original checks.
        var isInvalid = FindReferencedMethod(module, "System.Runtime.InteropServices.SafeHandle", "get_IsInvalid");
        var dispose = FindReferencedMethod(module, "System.Runtime.InteropServices.SafeHandle", "Dispose");
        var lastError = FindReferencedMethod(module, "System.Runtime.InteropServices.Marshal", "GetLastWin32Error");
        var win32Error = FindReferencedMethod(module, "System.ComponentModel.Win32Exception", ".ctor", "System.Int32");
        var stringFromChars = FindReferencedMethod(module, "System.String", ".ctor", "System.Char[]", "System.Int32", "System.Int32");
        var startsWith = FindReferencedMethod(module, "System.String", "StartsWith", "System.String", "System.StringComparison");
        var substring = FindReferencedMethod(module, "System.String", "Substring", "System.Int32");
        var concat = FindReferencedMethod(module, "System.String", "Concat", "System.String", "System.String");
        var fullPath = FindReferencedMethod(module, "System.IO.Path", "GetFullPath", "System.String");

        var resolve = new MethodDefinition("ResolveExecutablePath",
            MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.String);
        resolve.Parameters.Add(new ParameterDefinition("path", ParameterAttributes.None, module.TypeSystem.String));
        resolve.Body.InitLocals = true;
        var handle = new VariableDefinition(createFile.ReturnType);
        var buffer = new VariableDefinition(new ArrayType(module.TypeSystem.Char));
        var length = new VariableDefinition(module.TypeSystem.UInt32);
        var result = new VariableDefinition(module.TypeSystem.String);
        var openPath = new VariableDefinition(module.TypeSystem.String);
        resolve.Body.Variables.Add(handle);
        resolve.Body.Variables.Add(buffer);
        resolve.Body.Variables.Add(length);
        resolve.Body.Variables.Add(result);
        resolve.Body.Variables.Add(openPath);
        var il = resolve.Body.GetILProcessor();
        var open = Instruction.Create(OpCodes.Ldarg_0);
        var dosOpenPrefix = Instruction.Create(OpCodes.Ldstr, @"\\?\");
        var openFile = Instruction.Create(OpCodes.Ldloc, openPath);
        var tryStart = Instruction.Create(OpCodes.Ldloc, handle);
        var allocate = Instruction.Create(OpCodes.Ldc_I4, 32768);
        var pathFailure = Instruction.Create(OpCodes.Call, lastError);
        var decode = Instruction.Create(OpCodes.Ldloc, buffer);
        var checkDosPrefix = Instruction.Create(OpCodes.Ldloc, result);
        var canonicalize = Instruction.Create(OpCodes.Ldloc, result);
        var finallyStart = Instruction.Create(OpCodes.Ldloc, handle);
        var returnResult = Instruction.Create(OpCodes.Ldloc, result);

        // Keep the original caller's null-path error, and pin the opened file against writes/deletion.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, open);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        il.Append(open);
        il.Emit(OpCodes.Call, fullPath);
        il.Emit(OpCodes.Stloc, openPath);
        il.Emit(OpCodes.Ldloc, openPath);
        il.Emit(OpCodes.Ldstr, @"\\?\");
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Callvirt, startsWith);
        il.Emit(OpCodes.Brtrue, openFile);
        il.Emit(OpCodes.Ldloc, openPath);
        il.Emit(OpCodes.Ldstr, @"\\");
        il.Emit(OpCodes.Ldc_I4_5);
        il.Emit(OpCodes.Callvirt, startsWith);
        il.Emit(OpCodes.Brfalse, dosOpenPrefix);
        il.Emit(OpCodes.Ldstr, @"\\?\UNC\");
        il.Emit(OpCodes.Ldloc, openPath);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, substring);
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Stloc, openPath);
        il.Emit(OpCodes.Br, openFile);
        il.Append(dosOpenPrefix);
        il.Emit(OpCodes.Ldloc, openPath);
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Stloc, openPath);
        il.Append(openFile);
        il.Emit(OpCodes.Ldc_I4_0); // metadata-only access
        il.Emit(OpCodes.Ldc_I4_1); // FILE_SHARE_READ
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Ldc_I4_3); // OPEN_EXISTING
        il.Emit(OpCodes.Ldc_I4, 128); // FILE_ATTRIBUTE_NORMAL: follow the launch alias
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Call, createFile);
        il.Emit(OpCodes.Stloc, handle);
        il.Append(tryStart);
        il.Emit(OpCodes.Callvirt, isInvalid);
        il.Emit(OpCodes.Brfalse, allocate);
        il.Emit(OpCodes.Call, lastError);
        il.Emit(OpCodes.Newobj, win32Error);
        il.Emit(OpCodes.Throw);
        il.Append(allocate);
        il.Emit(OpCodes.Newarr, module.TypeSystem.Char);
        il.Emit(OpCodes.Stloc, buffer);
        il.Emit(OpCodes.Ldloc, handle);
        il.Emit(OpCodes.Ldloc, buffer);
        il.Emit(OpCodes.Ldc_I4, 32768);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, getFinalPath);
        il.Emit(OpCodes.Stloc, length);
        il.Emit(OpCodes.Ldloc, length);
        il.Emit(OpCodes.Brfalse, pathFailure);
        il.Emit(OpCodes.Ldloc, length);
        il.Emit(OpCodes.Ldc_I4, 32768);
        il.Emit(OpCodes.Blt_Un, decode);
        il.Append(pathFailure);
        il.Emit(OpCodes.Newobj, win32Error);
        il.Emit(OpCodes.Throw);
        il.Append(decode);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, length);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Newobj, stringFromChars);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldstr, @"\\?\UNC\");
        il.Emit(OpCodes.Ldc_I4_5); // OrdinalIgnoreCase
        il.Emit(OpCodes.Callvirt, startsWith);
        il.Emit(OpCodes.Brfalse, checkDosPrefix);
        il.Emit(OpCodes.Ldstr, @"\\");
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Callvirt, substring);
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Br, canonicalize);
        il.Append(checkDosPrefix);
        il.Emit(OpCodes.Ldstr, @"\\?\");
        il.Emit(OpCodes.Ldc_I4_4); // Ordinal
        il.Emit(OpCodes.Callvirt, startsWith);
        il.Emit(OpCodes.Brfalse, canonicalize);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Callvirt, substring);
        il.Emit(OpCodes.Stloc, result);
        il.Append(canonicalize);
        il.Emit(OpCodes.Call, fullPath);
        il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Call, rejectReparse);
        il.Emit(OpCodes.Ldloc, handle);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, validate);
        il.Emit(OpCodes.Leave, returnResult);
        il.Append(finallyStart);
        il.Emit(OpCodes.Callvirt, dispose);
        il.Emit(OpCodes.Endfinally);
        il.Append(returnResult);
        il.Emit(OpCodes.Ret);
        resolve.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
            TryStart = tryStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = returnResult
        });
        owner.Methods.Add(resolve);

        int registrationCalls = 0, workerCalls = 0;
        foreach (var type in AllTypes(module.Types))
        {
            bool registration = type.FullName.StartsWith(prefix + "PboToolboxRegistration/", StringComparison.Ordinal);
            bool worker = type.FullName.StartsWith(prefix + "PboModulePackage/", StringComparison.Ordinal);
            if (!registration && !worker) continue;
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var calls = new List<Instruction>();
                foreach (var instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called != null && called.DeclaringType.FullName == "System.Environment" && called.Name == "get_ProcessPath")
                        calls.Add(instruction);
                }
                if (calls.Count == 0) continue;
                ExpandShortBranches(method);
                foreach (var call in calls)
                {
                    method.Body.GetILProcessor().InsertAfter(call, Instruction.Create(OpCodes.Call, resolve));
                    if (registration) registrationCalls++; else workerCalls++;
                    changes.Add("Embedded PBO executable path resolution: " + type.FullName + "." + method.Name);
                }
            }
        }
        if (registrationCalls != 1 || workerCalls != 1)
            throw new InvalidOperationException("Unexpected PBO launch-path call count: " + registrationCalls + ", " + workerCalls);
    }

    private static MethodDefinition NamedMethod(TypeDefinition type, string name)
    {
        foreach (var method in type.Methods) if (method.Name == name) return method;
        throw new InvalidOperationException("Required method missing: " + type.FullName + "." + name);
    }

    private static FieldDefinition NamedField(TypeDefinition type, string name)
    {
        foreach (var field in type.Fields) if (field.Name == name) return field;
        throw new InvalidOperationException("Required field missing: " + type.FullName + "." + name);
    }

    private static void ReplaceAboutPage(ModuleDefinition module, string markup, List<string> changes)
    {
        var about = module.GetType("LaoYing.Optimizer.App.Views.AboutView");
        var initialize = NamedMethod(about, "InitializeComponent");
        var loaded = NamedField(about, "_contentLoaded");
        AssemblyNameReference presentationFramework = null;
        foreach (var reference in module.AssemblyReferences) if (reference.Name == "PresentationFramework") presentationFramework = reference;
        if (presentationFramework == null) throw new InvalidOperationException("WPF framework reference missing.");
        var xamlReader = new TypeReference("System.Windows.Markup", "XamlReader", module, presentationFramework);
        var parse = new MethodReference("Parse", module.TypeSystem.Object, xamlReader);
        parse.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var contentControl = new TypeReference("System.Windows.Controls", "ContentControl", module, presentationFramework);
        var setContent = new MethodReference("set_Content", module.TypeSystem.Void, contentControl) { HasThis = true };
        setContent.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        initialize.Body = new MethodBody(initialize);
        var il = initialize.Body.GetILProcessor();
        var done = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, loaded);
        il.Emit(OpCodes.Brtrue, done);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stfld, loaded);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, markup);
        il.Emit(OpCodes.Call, parse);
        il.Emit(OpCodes.Callvirt, setContent);
        il.Append(done);
        // The new page contains no links, payment images, event handlers or legacy BAML.
        foreach (var method in new List<MethodDefinition>(about.Methods))
            if (method != initialize && method.Name != ".ctor" && method.Name != "get_VersionLabel") about.Methods.Remove(method);
        foreach (var field in new List<FieldDefinition>(about.Fields)) if (field != loaded) about.Fields.Remove(field);
        foreach (var property in new List<PropertyDefinition>(about.Properties)) if (property.Name != "VersionLabel") about.Properties.Remove(property);
        about.Interfaces.Clear();
        about.NestedTypes.Clear();
        changes.Add("About page: introduction and version only; removed contact and payment UI.");
    }

    private static void EmbedGitHubUpdater(ModuleDefinition module, string runtimeAssemblyPath, string repository, string signingKeyId, List<string> changes)
    {
        const string helperResource = "Zhehao.Toolkit.Runtime.dll";
        module.Resources.Add(new EmbeddedResource(helperResource, ManifestResourceAttributes.Private, File.ReadAllBytes(runtimeAssemblyPath)));
        var moduleType = module.Types[0];
        foreach (var method in moduleType.Methods) if (method.Name == ".cctor") throw new InvalidOperationException("Original module initializer requires an explicit merge.");
        var bootstrap = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig |
            MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        bootstrap.Body.InitLocals = true;
        var stream = new VariableDefinition(module.ImportReference(typeof(Stream)));
        bootstrap.Body.Variables.Add(stream);
        var il = bootstrap.Body.GetILProcessor();
        il.Emit(OpCodes.Call, module.ImportReference(typeof(System.Reflection.Assembly).GetMethod("GetExecutingAssembly", Type.EmptyTypes)));
        il.Emit(OpCodes.Ldstr, helperResource);
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(System.Reflection.Assembly).GetMethod("GetManifestResourceStream", new Type[] { typeof(string) })));
        il.Emit(OpCodes.Stloc, stream);
        var tryStart = Instruction.Create(OpCodes.Call, module.ImportReference(typeof(System.Runtime.Loader.AssemblyLoadContext).GetProperty("Default").GetMethod));
        var finallyStart = Instruction.Create(OpCodes.Ldloc, stream);
        var done = Instruction.Create(OpCodes.Ret);
        il.Append(tryStart);
        il.Emit(OpCodes.Ldloc, stream);
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(System.Runtime.Loader.AssemblyLoadContext).GetMethod("LoadFromStream", new Type[] { typeof(Stream) })));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Leave, done);
        il.Append(finallyStart);
        il.Emit(OpCodes.Callvirt, module.ImportReference(typeof(IDisposable).GetMethod("Dispose")));
        il.Emit(OpCodes.Endfinally);
        il.Append(done);
        bootstrap.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
            TryStart = tryStart, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = done
        });
        moduleType.Methods.Add(bootstrap);

        using (var helper = AssemblyDefinition.ReadAssembly(runtimeAssemblyPath))
        {
            var helperType = helper.MainModule.GetType("Zhehao.Toolkit.Runtime.GitHubUpdates");
            var checkReference = module.ImportReference(NamedMethod(helperType, "CheckAsync"));
            MethodReference verifyPath = null, verifyHandle = null;
            foreach (var method in helperType.Methods)
                if (method.Name == "VerifyAsync")
                {
                    if (method.Parameters.Count == 3) verifyPath = module.ImportReference(method);
                    else if (method.Parameters.Count == 4) verifyHandle = module.ImportReference(method);
                }
            if (verifyPath == null || verifyHandle == null) throw new InvalidOperationException("Update verifier overload missing.");
            var viewModel = module.GetType("LaoYing.Optimizer.App.Presentation.UpdateViewModel");
            var checkRemote = NamedMethod(viewModel, "CheckRemoteAsync");
            var releaseType = ((GenericInstanceType)checkRemote.ReturnType).GenericArguments[0];
            var check = new GenericInstanceMethod(checkReference);
            check.GenericArguments.Add(releaseType);
            foreach (var attribute in new List<CustomAttribute>(checkRemote.CustomAttributes))
                if (attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute") checkRemote.CustomAttributes.Remove(attribute);
            checkRemote.Body = new MethodBody(checkRemote);
            var checkIl = checkRemote.Body.GetILProcessor();
            checkIl.Emit(OpCodes.Ldarg_0);
            checkIl.Emit(OpCodes.Ldfld, NamedField(viewModel, "_distribution"));
            checkIl.Emit(OpCodes.Callvirt, NamedMethod(module.GetType("LaoYing.Optimizer.App.Configuration.UpdateDistribution"), "get_GitHubRepository"));
            checkIl.Emit(OpCodes.Ldarg_0);
            checkIl.Emit(OpCodes.Call, NamedMethod(viewModel, "get_CurrentVersion"));
            checkIl.Emit(OpCodes.Ldarg_1);
            checkIl.Emit(OpCodes.Call, check);
            checkIl.Emit(OpCodes.Ret);
            int verifierCalls = 0, signerChecks = 0, repositoryChanges = 0, autoDownloads = 0;
            var pboFiles = module.GetType("LaoYing.Optimizer.App.Modules.Pbo.WindowsPboToolboxBindingFiles");
            var resolvePath = NamedMethod(pboFiles, "ResolveExecutablePath");
            foreach (var type in AllTypes(module.Types))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    var normalizePaths = new List<Instruction>();
                    var autoDownloadPoints = new List<Instruction>();
                    foreach (var instruction in method.Body.Instructions)
                    {
                        var called = instruction.Operand as MethodReference;
                        if (called != null && called.Name == "VerifyAsync" && called.DeclaringType.FullName == "LaoYing.Optimizer.Windows.Updates.WindowsUpdatePackageVerifier")
                        {
                            var verify = new GenericInstanceMethod(called.Parameters.Count == 2 ? verifyPath : verifyHandle);
                            verify.GenericArguments.Add(((GenericInstanceType)called.ReturnType).GenericArguments[0]);
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = verify;
                            verifierCalls++;
                        }
                        if (called != null && called.Name == "get_ProcessPath" && called.DeclaringType.FullName == "System.Environment" &&
                            type.FullName.StartsWith("LaoYing.Optimizer.App.Services.Updates.", StringComparison.Ordinal)) normalizePaths.Add(instruction);
                        if (called != null && called.Name == "set_IsChecking" && type.FullName == "LaoYing.Optimizer.App.Presentation.UpdateViewModel/<CheckAsync>d__101" &&
                            instruction.Previous.OpCode == OpCodes.Ldc_I4_0) autoDownloadPoints.Add(instruction);
                        if (instruction.OpCode != OpCodes.Ldstr) continue;
                        string text = instruction.Operand as string;
                        if (text == "C821A7362FEB20710109CADC247BA74494588B51" && type.FullName.StartsWith("LaoYing.Optimizer.App.Services.Updates.", StringComparison.Ordinal))
                        { instruction.Operand = signingKeyId; signerChecks++; }
                        if (type.FullName == "LaoYing.Optimizer.App.Configuration.UpdateDistribution")
                        {
                            if (text == "https://github.com/Lypc233/LaoYing-Toolkit") { instruction.Operand = "https://github.com/" + repository; repositoryChanges++; }
                            if (text != null && text.StartsWith("https://pan.quark.cn/", StringComparison.Ordinal)) instruction.Operand = "";
                        }
                        if (type.FullName.StartsWith("LaoYing.Optimizer.App.Presentation.UpdateViewModel", StringComparison.Ordinal) && text != null)
                            instruction.Operand = text.Replace("或使用夸克网盘", "或稍后重试").Replace("也可使用夸克下载入口。", "").Replace("请在正式签名的工具箱 EXE 中使用直接更新。", "请在正式版喆好工具箱 EXE 中使用直接更新。");
                        if (type.FullName == "LaoYing.Optimizer.App.Services.Updates.UpdateFileTransaction" && text == "老嘤工具箱 v") instruction.Operand = "喆好工具箱 v";
                    }
                    if (normalizePaths.Count > 0 || autoDownloadPoints.Count > 0) ExpandShortBranches(method);
                    var processor = method.Body.GetILProcessor();
                    foreach (var point in normalizePaths) processor.InsertAfter(point, Instruction.Create(OpCodes.Call, resolvePath));
                    foreach (var point in autoDownloadPoints)
                    {
                        var loadThis = Instruction.Create(OpCodes.Ldarg_0);
                        var loadOwner = Instruction.Create(OpCodes.Ldfld, NamedField(type, "<>4__this"));
                        var download = Instruction.Create(OpCodes.Call, NamedMethod(viewModel, "DownloadUpdateAsync"));
                        processor.InsertAfter(point, loadThis);
                        processor.InsertAfter(loadThis, loadOwner);
                        processor.InsertAfter(loadOwner, download);
                        processor.InsertAfter(download, Instruction.Create(OpCodes.Pop));
                        autoDownloads++;
                    }
                }
            }
            if (verifierCalls != 7 || signerChecks != 1 || repositoryChanges != 1 || autoDownloads != 1)
                throw new InvalidOperationException("Unexpected update patch counts: " + verifierCalls + ", " + signerChecks + ", " + repositoryChanges + ", " + autoDownloads);
            changes.Add("GitHub updates: " + repository + "; automatic downloads, signed manifests and SHA-256 verification.");
            changes.Add("Update transaction and restart flow retained; verified package calls: " + verifierCalls);
        }
    }

    private static string MethodSignature(MethodDefinition method)
    {
        var signature = new StringBuilder(method.FullName);
        signature.Append('|').Append(method.Attributes).Append('|').Append(method.ImplAttributes);
        if (method.HasPInvokeInfo)
            signature.Append('|').Append(method.PInvokeInfo.EntryPoint).Append('|').Append(method.PInvokeInfo.Attributes);
        if (!method.HasBody) return signature.ToString();
        foreach (var variable in method.Body.Variables) signature.Append('|').Append(variable.VariableType.FullName);
        foreach (var instruction in method.Body.Instructions)
        {
            signature.Append('|').Append(instruction.OpCode.Name).Append(':');
            var target = instruction.Operand as Instruction;
            var targets = instruction.Operand as Instruction[];
            if (target != null) signature.Append(method.Body.Instructions.IndexOf(target));
            else if (targets != null)
                foreach (var item in targets) signature.Append(method.Body.Instructions.IndexOf(item)).Append(',');
            else signature.Append(instruction.Operand);
        }
        foreach (var handler in method.Body.ExceptionHandlers)
            signature.Append('|').Append(handler.HandlerType).Append(':')
                .Append(method.Body.Instructions.IndexOf(handler.TryStart)).Append(',')
                .Append(method.Body.Instructions.IndexOf(handler.TryEnd)).Append(',')
                .Append(method.Body.Instructions.IndexOf(handler.HandlerStart)).Append(',')
                .Append(method.Body.Instructions.IndexOf(handler.HandlerEnd)).Append(',')
                .Append(handler.CatchType).Append(',')
                .Append(method.Body.Instructions.IndexOf(handler.FilterStart));
        return signature.ToString();
    }

    public static int VerifyPboGuards(string originalPath, string patchedPath)
    {
        const string prefix = "LaoYing.Optimizer.App.Modules.Pbo.";
        using (var original = AssemblyDefinition.ReadAssembly(originalPath))
        using (var patched = AssemblyDefinition.ReadAssembly(patchedPath))
        {
            var patchedMethods = new Dictionary<string, MethodDefinition>();
            foreach (var type in AllTypes(patched.MainModule.Types))
                foreach (var method in type.Methods) patchedMethods.Add(method.FullName, method);
            int verified = 0;
            foreach (var type in AllTypes(original.MainModule.Types))
            {
                bool protectedType = type.FullName == prefix + "PboToolboxBindingProtection" ||
                    type.FullName.StartsWith(prefix + "WindowsPboToolboxBindingFiles", StringComparison.Ordinal) ||
                    type.FullName == prefix + "PboToolboxRegistration/<RegisterAsync>d__1";
                foreach (var method in type.Methods)
                {
                    if (!protectedType && !(type.FullName == prefix + "PboModulePackage" && method.Name == "RejectReparsePoints")) continue;
                    MethodDefinition counterpart;
                    if (!patchedMethods.TryGetValue(method.FullName, out counterpart) ||
                        MethodSignature(method) != MethodSignature(counterpart))
                        throw new InvalidOperationException("Original PBO guard changed: " + method.FullName);
                    verified++;
                }
            }
            int patchedCalls = 0;
            foreach (var type in AllTypes(patched.MainModule.Types))
            {
                if (!type.FullName.StartsWith(prefix + "PboToolboxRegistration/", StringComparison.Ordinal) &&
                    !type.FullName.StartsWith(prefix + "PboModulePackage/", StringComparison.Ordinal)) continue;
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        var called = instruction.Operand as MethodReference;
                        if (called == null || called.DeclaringType.FullName != "System.Environment" || called.Name != "get_ProcessPath") continue;
                        var next = instruction.Next.Operand as MethodReference;
                        if (next == null || next.DeclaringType.FullName != prefix + "WindowsPboToolboxBindingFiles" || next.Name != "ResolveExecutablePath")
                            throw new InvalidOperationException("PBO launch path is not resolved.");
                        patchedCalls++;
                    }
                }
            }
            if (verified < 10 || patchedCalls != 2) throw new InvalidOperationException("Incomplete PBO patch verification.");
            return verified;
        }
    }

    public static string Patch(string input, string output, string assets, string runtime, bool inspectionBuild,
        string version, string repository, string signingKeyId, string updateRuntimePath, string aboutMarkup)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input));
        resolver.AddSearchDirectory(runtime);
        using (var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var changes = new List<string>();
            foreach (var type in AllTypes(assembly.MainModule.Types))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode != OpCodes.Ldstr) continue;
                        string original = instruction.Operand as string;
                        if (original == null) continue;
                        string replaced = ReplaceDisplayText(original);
                        if (original == replaced) continue;
                        instruction.Operand = replaced;
                        changes.Add("IL display string: " + type.FullName + "." + method.Name);
                    }
                }
            }
            foreach (var attribute in assembly.CustomAttributes)
            {
                if (attribute.AttributeType.FullName != "System.Reflection.AssemblyProductAttribute" &&
                    attribute.AttributeType.FullName != "System.Reflection.AssemblyTitleAttribute" &&
                    attribute.AttributeType.FullName != "System.Reflection.AssemblyDescriptionAttribute" &&
                    attribute.AttributeType.FullName != "System.Reflection.AssemblyFileVersionAttribute" &&
                    attribute.AttributeType.FullName != "System.Reflection.AssemblyInformationalVersionAttribute") continue;
                if (attribute.ConstructorArguments.Count == 1)
                {
                    string replacement = attribute.AttributeType.FullName == "System.Reflection.AssemblyFileVersionAttribute" ? version + ".0" :
                        attribute.AttributeType.FullName == "System.Reflection.AssemblyInformationalVersionAttribute" ? version : Brand;
                    attribute.ConstructorArguments[0] = new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, replacement);
                    changes.Add("Product metadata: " + attribute.AttributeType.Name);
                }
            }
            for (int resourceIndex = 0; resourceIndex < assembly.MainModule.Resources.Count; resourceIndex++)
            {
                var resource = assembly.MainModule.Resources[resourceIndex] as EmbeddedResource;
                if (resource == null || !resource.Name.EndsWith(".g.resources", StringComparison.Ordinal)) continue;
                using (var source = resource.GetResourceStream())
                using (var reader = new ResourceReader(source))
                using (var destination = new MemoryStream())
                {
                    using (var writer = new ResourceWriter(destination))
                    {
                        IDictionaryEnumerator enumerator = reader.GetEnumerator();
                        while (enumerator.MoveNext())
                        {
                            string key = (string)enumerator.Key;
                            if (key == "views/aboutview.baml" || key.StartsWith("assets/payment/", StringComparison.OrdinalIgnoreCase))
                            {
                                changes.Add("Removed unused About-page resource: " + key);
                                continue;
                            }
                            string resourceType;
                            byte[] data;
                            reader.GetResourceData(key, out resourceType, out data);
                            if (key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
                            {
                                int count = ReplaceSameLengthBytes(data, "LaoYing Toolkit", Brand, true);
                                count += ReplaceSameLengthBytes(data, "老嘤工具箱", Brand, false);
                                if (count != 0) changes.Add("BAML brand: " + key + " (" + count + ")");
                                if (key == "views/settingsview.baml")
                                {
                                    const string oldHint = "发现新版时在工具箱内提醒，后台通过托盘通知";
                                    string newHint = "自动连接 GitHub 下载新版，完成后可重启更新";
                                    int padding = Encoding.UTF8.GetByteCount(oldHint) - Encoding.UTF8.GetByteCount(newHint);
                                    if (padding < 0) throw new InvalidOperationException("Update hint exceeds its BAML slot.");
                                    int hints = ReplaceSameLengthBytes(data, oldHint, newHint + new string(' ', padding), false);
                                    if (hints != 1) throw new InvalidOperationException("Expected one automatic-update setting hint.");
                                    changes.Add("GitHub automatic-download setting hint: " + key);
                                }
                            }
                            string imagePath = null;
                            if (key == "assets/appicon-refined.png" || key == "assets/appicon-refined-light.png")
                                imagePath = Path.Combine(assets, "zhehao-512.png");
                            else if (key == "assets/appicon-refined.ico") imagePath = Path.Combine(assets, "zhehao.ico");
                            if (imagePath != null)
                            {
                                if (resourceType != "ResourceTypeCode.Stream") throw new InvalidOperationException("Unexpected WPF image resource type: " + resourceType);
                                byte[] image = File.ReadAllBytes(imagePath);
                                data = new byte[4 + image.Length];
                                Buffer.BlockCopy(BitConverter.GetBytes(image.Length), 0, data, 0, 4);
                                Buffer.BlockCopy(image, 0, data, 4, image.Length);
                                changes.Add("Brand icon: " + key);
                            }
                            writer.AddResourceData(key, resourceType, data);
                        }
                        writer.Generate();
                        byte[] newResource = destination.ToArray();
                        assembly.MainModule.Resources[resourceIndex] = new EmbeddedResource(resource.Name, resource.Attributes, newResource);
                    }
                }
            }
            EmbedPboExecutablePathResolution(assembly.MainModule, changes);
            ReplaceAboutPage(assembly.MainModule, aboutMarkup.Replace("__VERSION__", version), changes);
            EmbedGitHubUpdater(assembly.MainModule, updateRuntimePath, repository, signingKeyId, changes);
            if (inspectionBuild)
            {
                foreach (var type in AllTypes(assembly.MainModule.Types))
                {
                    if (!type.Name.Contains("ShutdownAfterSmokeDelayAsync")) continue;
                    foreach (var method in type.Methods)
                    {
                        if (!method.HasBody) continue;
                        foreach (var instruction in method.Body.Instructions)
                        {
                            var called = instruction.Operand as MethodReference;
                            if (called != null && called.Name == "FromSeconds" && called.DeclaringType.FullName == "System.TimeSpan")
                            {
                                var delay = instruction.Previous.Previous;
                                if (delay.OpCode != OpCodes.Ldc_I4_1 || instruction.Previous.OpCode != OpCodes.Conv_I8)
                                    throw new InvalidOperationException("Unexpected smoke-test timer IL.");
                                delay.OpCode = OpCodes.Ldc_I4;
                                delay.Operand = 300;
                                changes.Add("Inspection build: extended smoke-test delay.");
                                break;
                            }
                        }
                    }
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            assembly.Write(output);
            return String.Join(Environment.NewLine, changes);
        }
    }
}
