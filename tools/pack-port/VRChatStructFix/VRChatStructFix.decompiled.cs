using System;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Permissions;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Image;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.MethodInfo;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Type;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: InternalsVisibleTo("VrcFixTests")]
[assembly: TargetFramework(".NETCoreApp,Version=v6.0", FrameworkDisplayName = "")]
[assembly: AssemblyCompany("VRChatStructFix")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyProduct("VRChatStructFix")]
[assembly: AssemblyTitle("VRChatStructFix")]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[assembly: AssemblyVersion("1.0.0.0")]
[module: UnverifiableCode]
namespace VRChatStructFix;

internal static class DriftProbe
{
	internal static void Run(ManualLogSource log, INativeMethodInfoStructHandler stockMethod, INativeClassStructHandler stockClass, INativeImageStructHandler stockImage)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Expected O, but got Unknown
		List<string> list = new List<string>();
		try
		{
			ProbeMethodInfo(stockMethod, list);
			ProbeClass(stockClass, list);
			ProbeImage(stockImage, list);
		}
		catch (Exception ex)
		{
			log.LogWarning((object)("VRChatStructFix: drift probe threw (non-fatal): " + ex.Message));
			return;
		}
		if (list.Count == 0)
		{
			log.LogInfo((object)"VRChatStructFix: drift probe -- the stock handlers in BepInEx\\core read every probed member at the SAME offset we do. Nothing has been patched in place; our overrides are what makes the difference.");
			return;
		}
		bool flag = default(bool);
		BepInExInfoLogInterpolatedStringHandler val = new BepInExInfoLogInterpolatedStringHandler(134, 1, out flag);
		if (flag)
		{
			((BepInExLogInterpolatedStringHandler)val).AppendLiteral("VRChatStructFix: drift probe -- where the stock handlers currently in BepInEx\\core ");
			((BepInExLogInterpolatedStringHandler)val).AppendLiteral("read each member, vs where we do (");
			((BepInExLogInterpolatedStringHandler)val).AppendFormatted<int>(list.Count);
			((BepInExLogInterpolatedStringHandler)val).AppendLiteral(" members probed):");
		}
		log.LogInfo(val);
		foreach (string item in list)
		{
			log.LogInfo((object)("    " + item));
		}
	}

	private unsafe static void ProbeMethodInfo(INativeMethodInfoStructHandler stock, List<string> diffs)
	{
		IntPtr intPtr = Marshal.AllocHGlobal(88);
		try
		{
			byte* ptr = (byte*)(void*)intPtr;
			for (int i = 0; i < 88; i++)
			{
				ptr[i] = 0;
			}
			for (int j = 0; j <= 80; j += 8)
			{
				*(long*)(ptr + j) = -6196953087261802496L | (uint)j;
			}
			INativeMethodInfoStruct val = stock.Wrap((Il2CppMethodInfo*)(void*)intPtr);
			Compare(diffs, "MethodInfo.Name", 32, 24, Off(val.Name));
			Compare(diffs, "MethodInfo.Class", 40, 40, Off((IntPtr)val.Class));
			Compare(diffs, "MethodInfo.ReturnType", 24, 32, Off((IntPtr)val.ReturnType));
		}
		finally
		{
			Marshal.FreeHGlobal(intPtr);
		}
	}

	private unsafe static void ProbeClass(INativeClassStructHandler stock, List<string> diffs)
	{
		IntPtr intPtr = Marshal.AllocHGlobal(376);
		try
		{
			byte* ptr = (byte*)(void*)intPtr;
			for (int i = 0; i < 376; i++)
			{
				ptr[i] = 0;
			}
			for (int j = 0; j <= 304; j += 8)
			{
				*(long*)(ptr + j) = -6196953087261802496L | (uint)j;
			}
			INativeClassStruct val = stock.Wrap((Il2CppClass*)(void*)intPtr);
			Compare(diffs, "Il2CppClass.Name", 88, 144, Off(val.Name));
			Compare(diffs, "Il2CppClass.Namespace", 24, 24, Off(val.Namespace));
			Compare(diffs, "Il2CppClass.Methods", 160, 160, Off((IntPtr)val.Methods));
			Compare(diffs, "Il2CppClass.Fields", 144, 72, Off((IntPtr)val.Fields));
			Compare(diffs, "Il2CppClass.Parent", 120, 16, Off((IntPtr)val.Parent));
			Compare(diffs, "Il2CppClass.CastClass", 136, 88, Off((IntPtr)val.CastClass));
			Compare(diffs, "Il2CppClass.DeclaringType", 72, 112, Off((IntPtr)val.DeclaringType));
			Compare(diffs, "Il2CppClass.Class", 16, 136, Off((IntPtr)val.Class));
			Compare(diffs, "Il2CppClass.ElementClass", 64, 64, Off((IntPtr)val.ElementClass));
			Compare(diffs, "Il2CppClass.Image", 0, 0, Off((IntPtr)val.Image));
		}
		finally
		{
			Marshal.FreeHGlobal(intPtr);
		}
	}

	private unsafe static void ProbeImage(INativeImageStructHandler stock, List<string> diffs)
	{
		IntPtr intPtr = Marshal.AllocHGlobal(72);
		try
		{
			byte* ptr = (byte*)(void*)intPtr;
			for (int i = 0; i < 72; i++)
			{
				ptr[i] = 0;
			}
			for (int j = 0; j <= 64; j += 8)
			{
				*(long*)(ptr + j) = -6196953087261802496L | (uint)j;
			}
			INativeImageStruct val = stock.Wrap((Il2CppImage*)(void*)intPtr);
			Compare(diffs, "Il2CppImage.Name", 8, 0, Off(val.Name));
			Compare(diffs, "Il2CppImage.NameNoExt", 8, 8, Off(val.NameNoExt));
			Compare(diffs, "Il2CppImage.Assembly", 24, 16, Off((IntPtr)val.Assembly));
		}
		finally
		{
			Marshal.FreeHGlobal(intPtr);
		}
	}

	private static int Off(IntPtr sentinelValue)
	{
		ulong num = (ulong)(long)sentinelValue;
		if ((num & 0xFFFFFFFF00000000uL) != 12249790986447749120uL)
		{
			return -1;
		}
		return (int)num;
	}

	private static void Compare(List<string> diffs, string member, int ours, int vanillaStock, int stockRead)
	{
		if (stockRead < 0)
		{
			diffs.Add($"{member}: ours 0x{ours:X}, stock read an unrecognised location (member may be computed, not a plain field)");
		}
		else if (stockRead != ours)
		{
			diffs.Add($"{member}: ours 0x{ours:X}, stock 0x{stockRead:X}  <-- FIXED BY US");
		}
		else if (ours == vanillaStock)
		{
			diffs.Add($"{member}: 0x{ours:X} -- identical in stock and VRChat, nothing to fix");
		}
		else
		{
			diffs.Add($"{member}: 0x{ours:X} -- stock reads OUR offset although vanilla Il2CppInterop uses 0x{vanillaStock:X}: Il2CppInterop.Runtime.dll has been PATCHED IN PLACE for this member. " + "Both fixes agree, so the result is correct, but you now have two copies of the same fix.");
		}
	}
}
internal static class HandlerInstaller
{
	internal readonly struct InstallResult
	{
		public readonly string OldHandler;

		public readonly string NewHandler;

		public readonly bool InsertedIntoVersionedHandlers;

		public InstallResult(string oldHandler, string newHandler, bool inserted)
		{
			OldHandler = oldHandler;
			NewHandler = newHandler;
			InsertedIntoVersionedHandlers = inserted;
		}
	}

	private static readonly Type UVH = typeof(UnityVersionHandler);

	private const BindingFlags SF = BindingFlags.Static | BindingFlags.NonPublic;

	private static FieldInfo RequireField(string name)
	{
		FieldInfo? field = UVH.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
		if (field == null)
		{
			throw new MissingFieldException("VRChatStructFix: Il2CppInterop.Runtime.Runtime.UnityVersionHandler has no field '" + name + "'. The Il2CppInterop version in BepInEx\\core has changed and this patcher must be updated. Refusing to guess.");
		}
		return field;
	}

	internal static object GetCurrent(string fieldName)
	{
		return RequireField(fieldName).GetValue(null);
	}

	internal static InstallResult Install(Type iface, string fieldName, object handler)
	{
		if (iface == null)
		{
			throw new ArgumentNullException("iface");
		}
		if (handler == null)
		{
			throw new ArgumentNullException("handler");
		}
		if (!iface.IsInstanceOfType(handler))
		{
			throw new ArgumentException("VRChatStructFix: " + handler.GetType().FullName + " does not implement " + iface.FullName + ". This almost always means a SECOND copy of Il2CppInterop.Runtime.dll got loaded (check that nothing copied it next to the patcher).");
		}
		FieldInfo fieldInfo = RequireField(fieldName);
		object value = fieldInfo.GetValue(null);
		bool inserted = TryInsertVersionedHandler(iface, handler);
		fieldInfo.SetValue(null, handler);
		SetHandlersCacheEntry(iface, handler);
		return new InstallResult((value == null) ? "<null>" : value.GetType().FullName, handler.GetType().FullName, inserted);
	}

	internal static bool IsInstalled(string fieldName, object handler)
	{
		return RequireField(fieldName).GetValue(null) == handler;
	}

	private static bool TryInsertVersionedHandler(Type iface, object handler)
	{
		IDictionary dictionary = (IDictionary)RequireField("VersionedHandlers").GetValue(null);
		if (dictionary == null || !dictionary.Contains(iface))
		{
			return false;
		}
		IList list = (IList)dictionary[iface];
		if (list == null)
		{
			return false;
		}
		Type type = list.GetType().GetGenericArguments()[0];
		FieldInfo field = type.GetField("Item2");
		if (field == null)
		{
			return false;
		}
		for (int i = 0; i < list.Count; i++)
		{
			if (field.GetValue(list[i]) == handler)
			{
				return false;
			}
		}
		object value = Activator.CreateInstance(type, new Version(0, 0, 0), handler);
		list.Insert(0, value);
		return true;
	}

	private static void SetHandlersCacheEntry(Type iface, object handler)
	{
		IDictionary dictionary = (IDictionary)RequireField("Handlers").GetValue(null);
		if (dictionary != null)
		{
			dictionary[iface] = handler;
		}
	}

	internal static IEnumerable<string> DescribeCurrent(params string[] fieldNames)
	{
		foreach (string text in fieldNames)
		{
			object obj = null;
			try
			{
				obj = GetCurrent(text);
			}
			catch
			{
			}
			yield return text + " = " + ((obj == null) ? "<null>" : obj.GetType().FullName);
		}
	}
}
[PatcherPluginInfo("vrchatarchive.vrcstructfix", "VRChat IL2CPP Struct Layout Fix", "1.0.0")]
public class LayoutFixPatcher : BasePatcher
{
	public const string Guid = "vrchatarchive.vrcstructfix";

	private static VrcMethodInfoStructHandler _method;

	private static VrcClassStructHandler _class;

	private static VrcImageStructHandler _image;

	private static bool _installedOnce;

	public override void Initialize()
	{
		try
		{
			// Before anything else: repair field offsets process-wide, so nothing caches a wrong one.
			FieldOffsetGlobalFix.Install(((BasePatcher)this).Log);
			VrcClassStructHandler.AllowUnverifiedFields = Environment.GetEnvironmentVariable("VRCHATSTRUCTFIX_ALLOW_UNVERIFIED") == "1";
			InstallAll(!_installedOnce);
			_installedOnce = true;
			if (Environment.GetEnvironmentVariable("VRCHATSTRUCTFIX_SELFTEST") != "1")
			{
				((BasePatcher)this).Log.LogWarning((object)"VRChatStructFix: self-test skipped (VRCHATSTRUCTFIX_NO_SELFTEST=1). Handlers are installed but unverified.");
			}
			else if (SelfTest.Run(((BasePatcher)this).Log))
			{
				((BasePatcher)this).Log.LogMessage((object)"VRChatStructFix: active. il2cpp method/class/image names now read correctly.");
			}
			else
			{
				((BasePatcher)this).Log.LogWarning((object)"VRChatStructFix: handlers are installed but the self-test did not fully pass. See the [SELF-TEST] lines above before trusting anything downstream.");
			}
		}
		catch (Exception ex)
		{
			((BasePatcher)this).Log.LogError((object)"VRChatStructFix: INSTALL FAILED, leaving Il2CppInterop's stock handlers in place.");
			((BasePatcher)this).Log.LogError((object)ex);
		}
	}

	public override void Finalizer()
	{
		try
		{
			if (_installedOnce && (!HandlerInstaller.IsInstalled("methodInfoStructHandler", _method) || !HandlerInstaller.IsInstalled("classStructHandler", _class) || !HandlerInstaller.IsInstalled("imageStructHandler", _image)))
			{
				((BasePatcher)this).Log.LogWarning((object)"VRChatStructFix: a handler was reverted between Initialize() and Finalizer() (something re-ran UnityVersionHandler.RecalculateHandlers). Re-asserting.");
				InstallAll(firstRun: false);
			}
		}
		catch (Exception ex)
		{
			((BasePatcher)this).Log.LogError((object)("VRChatStructFix: re-assert in Finalizer() failed: " + ex));
		}
	}

	private void InstallAll(bool firstRun)
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Expected O, but got Unknown
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Expected O, but got Unknown
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Expected O, but got Unknown
		if (firstRun)
		{
			INativeMethodInfoStructHandler val = (INativeMethodInfoStructHandler)HandlerInstaller.GetCurrent("methodInfoStructHandler");
			INativeClassStructHandler val2 = (INativeClassStructHandler)HandlerInstaller.GetCurrent("classStructHandler");
			INativeImageStructHandler val3 = (INativeImageStructHandler)HandlerInstaller.GetCurrent("imageStructHandler");
			if (val == null || val2 == null || val3 == null)
			{
				throw new InvalidOperationException("VRChatStructFix: UnityVersionHandler has not elected its handlers yet (methodInfo=" + Describe(val) + ", class=" + Describe(val2) + ", image=" + Describe(val3) + "). Installing now would be pointless. This means the BepInEx startup order changed.");
			}
			_method = new VrcMethodInfoStructHandler(val);
			_class = new VrcClassStructHandler(val2);
			_image = new VrcImageStructHandler(val3);
			((BasePatcher)this).Log.LogInfo((object)($"VRChatStructFix: struct sizes reported by the stock handlers -- MethodInfo={((INativeStructHandler)val).Size()} (0x{((INativeStructHandler)val).Size():X}), Class={((INativeStructHandler)val2).Size()} (0x{((INativeStructHandler)val2).Size():X}), Image={((INativeStructHandler)val3).Size()} (0x{((INativeStructHandler)val3).Size():X}). " + "All three match VRChat, so sizes are forwarded unchanged."));
			DriftProbe.Run(((BasePatcher)this).Log, val, val2, val3);
			if (VrcClassStructHandler.AllowUnverifiedFields)
			{
				((BasePatcher)this).Log.LogWarning((object)"VRChatStructFix: VRCHATSTRUCTFIX_ALLOW_UNVERIFIED=1 -- INativeClassStruct.ImplementedInterfaces / .TypeHierarchy will be read at the STOCK offsets, which are wrong on VRChat. Expect memory corruption if anything injects a custom MonoBehaviour or enum.");
			}
		}
		Report("MethodInfo", HandlerInstaller.Install(typeof(INativeMethodInfoStructHandler), "methodInfoStructHandler", _method));
		Report("Il2CppClass", HandlerInstaller.Install(typeof(INativeClassStructHandler), "classStructHandler", _class));
		Report("Il2CppImage", HandlerInstaller.Install(typeof(INativeImageStructHandler), "imageStructHandler", _image));
	}

	private void Report(string what, HandlerInstaller.InstallResult r)
	{
		((BasePatcher)this).Log.LogWarning((object)($"VRChatStructFix: {what} handler  {r.OldHandler}  ->  {r.NewHandler}" + (r.InsertedIntoVersionedHandlers ? "  (+pinned at VersionedHandlers[0])" : "  (re-asserted)")));
	}

	private static string Describe(object o)
	{
		if (o != null)
		{
			return o.GetType().Name;
		}
		return "<null>";
	}
}
internal static class SelfTest
{
	private const int MaxMethodsToScan = 64;

	private const ulong MinPlausiblePointer = 65536uL;

	private const ulong MaxPlausiblePointer = 140737488355327uL;

	internal unsafe static bool Run(ManualLogSource log)
	{
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a3: Expected O, but got Unknown
		//IL_0233: Unknown result type (might be due to invalid IL or missing references)
		//IL_023a: Expected O, but got Unknown
		//IL_039e: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a5: Expected O, but got Unknown
		//IL_0414: Unknown result type (might be due to invalid IL or missing references)
		//IL_041b: Expected O, but got Unknown
		//IL_050f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0516: Expected O, but got Unknown
		//IL_0564: Unknown result type (might be due to invalid IL or missing references)
		//IL_056b: Expected O, but got Unknown
		bool flag = true;
		try
		{
			IntPtr il2CppClass = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Object");
			if (il2CppClass == IntPtr.Zero)
			{
				log.LogError((object)"[SELF-TEST] FAIL: could not resolve System.Object from mscorlib.dll. Either the il2cpp runtime is not up yet, or the obfuscated export mapping for il2cpp_domain_get / il2cpp_domain_get_assemblies / il2cpp_class_from_name is wrong. The struct handlers are installed regardless.");
				return false;
			}
			List<IntPtr> list = new List<IntPtr>();
			IntPtr zero = IntPtr.Zero;
			IntPtr item;
			while ((item = IL2CPP.il2cpp_class_get_methods(il2CppClass, ref zero)) != IntPtr.Zero && list.Count < 64)
			{
				list.Add(item);
			}
			if (list.Count == 0)
			{
				log.LogError((object)"[SELF-TEST] FAIL: il2cpp_class_get_methods returned nothing for System.Object. That is an EXPORT MAPPING problem (il2cpp_class_get_methods), not a struct layout problem -- the handlers cannot help with it.");
				return false;
			}
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			string text = null;
			List<string> list2 = new List<string>();
			foreach (IntPtr item2 in list)
			{
				if (!LooksLikePointer((ulong)(long)item2))
				{
					num2++;
					continue;
				}
				INativeMethodInfoStruct val = UnityVersionHandler.Wrap((Il2CppMethodInfo*)(void*)item2);
				if (val == null)
				{
					num2++;
					continue;
				}
				IntPtr name = val.Name;
				string text2 = (LooksLikePointer((ulong)(long)name) ? Marshal.PtrToStringAnsi(name) : null);
				if (IsSaneIdentifier(text2))
				{
					num++;
					list2.Add(text2);
				}
				else
				{
					num2++;
					if (text == null)
					{
						text = Describe(name, text2);
					}
				}
				if ((IntPtr)val.Class != il2CppClass)
				{
					num3++;
				}
			}
			bool flag2 = list2.Contains("Equals");
			bool flag3 = default(bool);
			if (num > 0 && num2 == 0 && num3 == 0 && flag2)
			{
				BepInExMessageLogInterpolatedStringHandler val2 = new BepInExMessageLogInterpolatedStringHandler(121, 3, out flag3);
				if (flag3)
				{
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("[SELF-TEST] PASS  MethodInfo: ");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<int>(num);
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("/");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<int>(list.Count);
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral(" System.Object methods ");
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("resolved to sane names, all report klass == System.Object. ");
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("Sample: ");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(string.Join(", ", list2.GetRange(0, Math.Min(6, list2.Count))));
				}
				log.LogMessage(val2);
			}
			else
			{
				flag = false;
				BepInExErrorLogInterpolatedStringHandler val3 = new BepInExErrorLogInterpolatedStringHandler(105, 6, out flag3);
				if (flag3)
				{
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("[SELF-TEST] FAIL  MethodInfo: sane=");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<int>(num);
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral(" garbage=");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<int>(num2);
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral(" ");
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("wrong-klass=");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<int>(num3);
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral(" \"Equals\"-found=");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<bool>(flag2);
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral(" ");
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("(of ");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<int>(list.Count);
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral(" methods). First bad name: ");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<string>(text ?? "<none>");
				}
				log.LogError(val3);
			}
			INativeClassStruct obj = UnityVersionHandler.Wrap((Il2CppClass*)(void*)il2CppClass);
			string text3 = ReadAnsi(obj.Name);
			string text4 = ReadAnsi(obj.Namespace);
			ushort methodCount = obj.MethodCount;
			Il2CppMethodInfo** methods = obj.Methods;
			bool flag4 = false;
			if (methods != null && LooksLikePointer((ulong)(long)(IntPtr)methods) && methodCount > 0 && methodCount <= 4096)
			{
				flag4 = (IntPtr)(*methods) == list[0];
			}
			if (text3 == "Object" && text4 == "System" && flag4 && methodCount >= list.Count)
			{
				BepInExMessageLogInterpolatedStringHandler val2 = new BepInExMessageLogInterpolatedStringHandler(111, 3, out flag3);
				if (flag3)
				{
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("[SELF-TEST] PASS  Il2CppClass: name=\"");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(text3);
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("\" namespace=\"");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(text4);
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("\" ");
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("method_count=");
					((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<ushort>(methodCount);
					((BepInExLogInterpolatedStringHandler)val2).AppendLiteral(", methods[0] matches il2cpp_class_get_methods.");
				}
				log.LogMessage(val2);
			}
			else
			{
				flag = false;
				BepInExErrorLogInterpolatedStringHandler val3 = new BepInExErrorLogInterpolatedStringHandler(125, 4, out flag3);
				if (flag3)
				{
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("[SELF-TEST] FAIL  Il2CppClass: name=\"");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<string>(text3 ?? "<null>");
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("\" (expected \"Object\") ");
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("namespace=\"");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<string>(text4 ?? "<null>");
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("\" (expected \"System\") method_count=");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<ushort>(methodCount);
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral(" ");
					((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("methods[0]-matches=");
					((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<bool>(flag4);
				}
				log.LogError(val3);
			}
			Il2CppImage* image = obj.Image;
			if (image != null && LooksLikePointer((ulong)(long)(IntPtr)image))
			{
				INativeImageStruct obj2 = UnityVersionHandler.Wrap(image);
				string text5 = ReadAnsi(obj2.Name);
				string text6 = ReadAnsi(obj2.NameNoExt);
				if (text5 == "mscorlib.dll" && text6 == "mscorlib")
				{
					BepInExMessageLogInterpolatedStringHandler val2 = new BepInExMessageLogInterpolatedStringHandler(51, 2, out flag3);
					if (flag3)
					{
						((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("[SELF-TEST] PASS  Il2CppImage: name=\"");
						((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(text5);
						((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("\" nameNoExt=\"");
						((BepInExLogInterpolatedStringHandler)val2).AppendFormatted<string>(text6);
						((BepInExLogInterpolatedStringHandler)val2).AppendLiteral("\"");
					}
					log.LogMessage(val2);
				}
				else
				{
					flag = false;
					BepInExErrorLogInterpolatedStringHandler val3 = new BepInExErrorLogInterpolatedStringHandler(99, 2, out flag3);
					if (flag3)
					{
						((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("[SELF-TEST] FAIL  Il2CppImage: name=\"");
						((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<string>(text5 ?? "<null>");
						((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("\" ");
						((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("(expected \"mscorlib.dll\") nameNoExt=\"");
						((BepInExLogInterpolatedStringHandler)val3).AppendFormatted<string>(text6 ?? "<null>");
						((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("\" ");
						((BepInExLogInterpolatedStringHandler)val3).AppendLiteral("(expected \"mscorlib\")");
					}
					log.LogError(val3);
				}
			}
			else
			{
				flag = false;
				log.LogError((object)"[SELF-TEST] FAIL  Il2CppImage: klass->image (offset 0x00) is not a plausible pointer.");
			}
			try
			{
				string text7 = IL2CPP.il2cpp_class_get_name_(il2CppClass);
				string text8 = IL2CPP.il2cpp_class_get_namespace_(il2CppClass);
				if (text7 != "Object")
				{
					log.LogWarning((object)("[SELF-TEST] NOTE  native il2cpp_class_get_name_ returned \"" + text7 + "\" instead of \"Object\". That export's obfuscated name is mismapped in Il2CppInterop.Runtime.dll -- NOT something this patcher can fix."));
				}
				if (text8 != "System")
				{
					log.LogWarning((object)("[SELF-TEST] NOTE  native il2cpp_class_get_namespace_ returned \"" + Trim(text8) + "\" instead of \"System\". Known issue: this pack maps il2cpp_class_get_namespace to an export that reads klass+0x28 while namespaze lives at klass+0x18. Our INativeClassStruct.Namespace is correct; the raw P/Invoke is not. NOT fixable from a patcher."));
				}
			}
			catch (Exception ex)
			{
				log.LogWarning((object)("[SELF-TEST] NOTE  native class name/namespace cross-check threw: " + ex.Message));
			}
			log.LogMessage((object)(flag ? "[SELF-TEST] ===== OVERALL: PASS -- VRChat il2cpp struct layout handled correctly =====" : "[SELF-TEST] ===== OVERALL: FAIL -- see the lines above ====="));
		}
		catch (Exception ex2)
		{
			flag = false;
			log.LogError((object)("[SELF-TEST] threw, the game keeps running: " + ex2));
		}
		return flag;
	}

	private static bool LooksLikePointer(ulong v)
	{
		if (v >= 65536)
		{
			return v <= 140737488355327L;
		}
		return false;
	}

	private static string ReadAnsi(IntPtr p)
	{
		if (!LooksLikePointer((ulong)(long)p))
		{
			return null;
		}
		return Marshal.PtrToStringAnsi(p);
	}

	private static bool IsSaneIdentifier(string s)
	{
		if (string.IsNullOrEmpty(s) || s.Length > 512)
		{
			return false;
		}
		foreach (char c in s)
		{
			if (c < ' ' || c > '~')
			{
				return false;
			}
		}
		return true;
	}

	private static string Describe(IntPtr p, string s)
	{
		return "ptr=0x" + ((ulong)(long)p).ToString("X") + " text=" + ((s == null) ? "<unreadable>" : ("\"" + Trim(s) + "\""));
	}

	private static string Trim(string s)
	{
		if (s != null)
		{
			if (s.Length <= 48)
			{
				return s;
			}
			return s.Substring(0, 48) + "...";
		}
		return "<null>";
	}
}
internal sealed class VrcClassStructHandler : INativeClassStructHandler, INativeStructHandler
{
	internal sealed class Wrapper : INativeClassStruct, INativeStruct
	{
		private unsafe readonly byte* _p;

		public unsafe IntPtr Pointer => (IntPtr)_p;

		public unsafe Il2CppClass* ClassPointer => (Il2CppClass*)_p;

		public unsafe IntPtr VTable => IntPtr.Add((IntPtr)_p, 312);

		public unsafe ref IntPtr Name => ref *(IntPtr*)(_p + 152);

		public unsafe ref Il2CppClass* Parent => ref *(Il2CppClass**)(_p + 120);

		public unsafe ref Il2CppClass* CastClass => ref *(Il2CppClass**)(_p + 136);

		public unsafe ref Il2CppClass* DeclaringType => ref *(Il2CppClass**)(_p + 128);

		public unsafe ref Il2CppClass* Class => ref *(Il2CppClass**)(_p + 16);

		public unsafe ref Il2CppFieldInfo* Fields => ref *(Il2CppFieldInfo**)(_p + 80);

		public unsafe ref Il2CppMethodInfo** Methods => ref *(Il2CppMethodInfo***)(_p + 168);

		public unsafe ref Il2CppImage* Image => ref *(Il2CppImage**)_p;

		public unsafe ref IntPtr Namespace => ref *(IntPtr*)(_p + 24);

		public unsafe ref Il2CppClass* ElementClass => ref *(Il2CppClass**)(_p + 64);

		public unsafe ref Il2CppRuntimeInterfaceOffsetPair* InterfaceOffsets => ref *(Il2CppRuntimeInterfaceOffsetPair**)(_p + 176);

		public unsafe ref uint InstanceSize => ref *(uint*)(_p + 248);

		public unsafe ref uint ActualSize => ref *(uint*)(_p + 256);

		public unsafe ref int NativeSize => ref *(int*)(_p + 264);

		public unsafe ref Il2CppClassAttributes Flags => ref *(Il2CppClassAttributes*)(_p + 280);

		public unsafe ref ushort MethodCount => ref *(ushort*)(_p + 288);

		public unsafe ref ushort FieldCount => ref *(ushort*)(_p + 292);

		public unsafe ref ushort VtableCount => ref *(ushort*)(_p + 298);

		public unsafe ref ushort InterfaceCount => ref *(ushort*)(_p + 300);

		public unsafe ref ushort InterfaceOffsetsCount => ref *(ushort*)(_p + 302);

		public unsafe ref byte TypeHierarchyDepth => ref _p[304];

		public unsafe INativeTypeStruct ByValArg => UnityVersionHandler.Wrap((Il2CppTypeStruct*)(_p + 32));

		public unsafe INativeTypeStruct ThisArg => UnityVersionHandler.Wrap((Il2CppTypeStruct*)(_p + 48));

		public bool ValueType
		{
			get
			{
				if (ByValArg.ValueType)
				{
					return ThisArg.ValueType;
				}
				return false;
			}
			set
			{
			}
		}

		public bool EnumType
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 309, 2);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 309, 2, value);
			}
		}

		public bool IsGeneric
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 309, 4);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 309, 4, value);
			}
		}

		public bool Initialized
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 309, 1);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 309, 1, value);
			}
		}

		public bool InitializedAndNoError
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 309, 0);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 309, 0, value);
			}
		}

		public bool SizeInited
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 310, 0);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 310, 0, value);
			}
		}

		public bool HasFinalize
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 310, 1);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 310, 1, value);
			}
		}

		public bool IsVtableInitialized
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 310, 5);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 310, 5, value);
			}
		}

		public unsafe ref Il2CppClass** ImplementedInterfaces => ref *(Il2CppClass***)(_p + 80);

		public unsafe ref Il2CppClass** TypeHierarchy => ref *(Il2CppClass***)(_p + 200);

		internal unsafe Wrapper(IntPtr p)
		{
			_p = (byte*)(void*)p;
		}
	}

	internal const string UnmeasuredMessage = "VRChatStructFix: this Il2CppClass field has no verified offset on VRChat's il2cpp build. Reading it at the stock Il2CppInterop offset would silently corrupt memory, so it is refused instead. This only affects ClassInjector / EnumInjector (injecting custom MonoBehaviours or enums into il2cpp), which is not supported by this fix yet. Set VRCHATSTRUCTFIX_ALLOW_UNVERIFIED=1 to fall back to the (wrong) stock offsets.";

	internal static bool AllowUnverifiedFields;

	private const int StockImplementedInterfaces = 128;

	private const int StockTypeHierarchy = 200;

	private readonly INativeClassStructHandler _stock;

	internal INativeClassStructHandler Stock => _stock;

	internal VrcClassStructHandler(INativeClassStructHandler stock)
	{
		_stock = stock ?? throw new ArgumentNullException("stock");
	}

	public int Size()
	{
		return ((INativeStructHandler)_stock).Size();
	}

	public INativeClassStruct CreateNewStruct(int vTableSlots)
	{
		INativeClassStruct val = _stock.CreateNewStruct(vTableSlots);
		if (val != null)
		{
			return (INativeClassStruct)(object)new Wrapper(((INativeStruct)val).Pointer);
		}
		return null;
	}

	public unsafe INativeClassStruct Wrap(Il2CppClass* classPointer)
	{
		if (classPointer != null)
		{
			return (INativeClassStruct)(object)new Wrapper((IntPtr)classPointer);
		}
		return null;
	}
}
internal sealed class VrcImageStructHandler : INativeImageStructHandler, INativeStructHandler
{
	internal sealed class Wrapper : INativeImageStruct, INativeStruct
	{
		private unsafe readonly byte* _p;

		public unsafe IntPtr Pointer => (IntPtr)_p;

		public unsafe Il2CppImage* ImagePointer => (Il2CppImage*)_p;

		public unsafe ref IntPtr Name => ref *(IntPtr*)(_p + 0);

		public unsafe ref IntPtr NameNoExt => ref *(IntPtr*)(_p + 8);

		public unsafe ref Il2CppAssembly* Assembly => ref *(Il2CppAssembly**)(_p + 24);

		public unsafe ref byte Dynamic => ref _p[16];

		public bool HasNameNoExt => true;

		internal unsafe Wrapper(IntPtr p)
		{
			_p = (byte*)(void*)p;
		}
	}

	private readonly INativeImageStructHandler _stock;

	internal INativeImageStructHandler Stock => _stock;

	internal VrcImageStructHandler(INativeImageStructHandler stock)
	{
		_stock = stock ?? throw new ArgumentNullException("stock");
	}

	public int Size()
	{
		return ((INativeStructHandler)_stock).Size();
	}

	public INativeImageStruct CreateNewStruct()
	{
		INativeImageStruct val = _stock.CreateNewStruct();
		if (val != null)
		{
			return (INativeImageStruct)(object)new Wrapper(((INativeStruct)val).Pointer);
		}
		return null;
	}

	public unsafe INativeImageStruct Wrap(Il2CppImage* imagePointer)
	{
		if (imagePointer != null)
		{
			return (INativeImageStruct)(object)new Wrapper((IntPtr)imagePointer);
		}
		return null;
	}
}
internal sealed class VrcMethodInfoStructHandler : INativeMethodInfoStructHandler, INativeStructHandler
{
	internal sealed class Wrapper : INativeMethodInfoStruct, INativeStruct
	{
		private unsafe readonly byte* _p;

		public unsafe IntPtr Pointer => (IntPtr)_p;

		public unsafe Il2CppMethodInfo* MethodInfoPointer => (Il2CppMethodInfo*)_p;

		public unsafe ref IntPtr Name => ref *(IntPtr*)(_p + 32);

		public unsafe ref Il2CppClass* Class => ref *(Il2CppClass**)(_p + 24);

		public unsafe ref Il2CppTypeStruct* ReturnType => ref *(Il2CppTypeStruct**)(_p + 40);

		public unsafe ref IntPtr MethodPointer => ref *(IntPtr*)_p;

		public unsafe ref IntPtr VirtualMethodPointer => ref *(IntPtr*)(_p + 8);

		public unsafe ref IntPtr InvokerMethod => ref *(IntPtr*)(_p + 16);

		public unsafe ref Il2CppParameterInfo* Parameters => ref *(Il2CppParameterInfo**)(_p + 48);

		public unsafe ref uint Token => ref *(uint*)(_p + 72);

		// 0x4C, NOT 0x4E. il2cpp_method_get_flags is `uint16 f(MethodInfo*, uint32* iflags)`: it WRITES
		// word[0x4E] (iflags) through the out-parameter and RETURNS word[0x4C] (the flags). Deriving the
		// offset by taking the first [rcx+disp] the function touches therefore lands on iflags, and that
		// is how 78 got here. il2cpp_method_is_instance settles it -- `byte[0x4C] >> 4 & 1`, inverted --
		// so METHOD_STATIC (0x10) lives in the word at 0x4C. The surrounding layout agrees: token u32 at
		// 0x48, flags u16 at 0x4C, iflags u16 at 0x4E, slot u16 at 0x50, parameters_count u8 at 0x52.
		// The two-byte slip was not subtle: every static method read as an instance method, so
		// MemberAlign's shape for each one lost its leading '@' and contradicted the recorded shape.
		// Hundreds of correctly-placed members were rejected as mismatches on that basis alone.
		public unsafe ref Il2CppMethodFlags Flags => ref *(Il2CppMethodFlags*)(_p + 76);

		public unsafe ref ushort Slot => ref *(ushort*)(_p + 80);

		public unsafe ref byte ParametersCount => ref _p[82];

		public bool IsGeneric
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 83, 0);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 83, 0, value);
			}
		}

		public bool IsInflated
		{
			get
			{
				return NativeStructUtils.CheckBit((INativeStruct)(object)this, 83, 1);
			}
			set
			{
				NativeStructUtils.SetBit((INativeStruct)(object)this, 83, 1, value);
			}
		}

		public bool IsMarshalledFromNative
		{
			get
			{
				return false;
			}
			set
			{
			}
		}

		internal unsafe Wrapper(IntPtr p)
		{
			_p = (byte*)(void*)p;
		}
	}

	private readonly INativeMethodInfoStructHandler _stock;

	internal INativeMethodInfoStructHandler Stock => _stock;

	internal VrcMethodInfoStructHandler(INativeMethodInfoStructHandler stock)
	{
		_stock = stock ?? throw new ArgumentNullException("stock");
	}

	public int Size()
	{
		return ((INativeStructHandler)_stock).Size();
	}

	public INativeMethodInfoStruct CreateNewStruct()
	{
		INativeMethodInfoStruct val = _stock.CreateNewStruct();
		if (val != null)
		{
			return (INativeMethodInfoStruct)(object)new Wrapper(((INativeStruct)val).Pointer);
		}
		return null;
	}

	public unsafe INativeMethodInfoStruct Wrap(Il2CppMethodInfo* methodPointer)
	{
		if (methodPointer != null)
		{
			return (INativeMethodInfoStruct)(object)new Wrapper((IntPtr)methodPointer);
		}
		return null;
	}
}
// GLOBAL FIELD-OFFSET REPAIR (added 2026-09-16).
//
// Il2CppInterop computes every field access as *(T*)(object + fieldOffset) and gets that offset
// from its own reimplementation of il2cpp_field_get_offset, which reads FieldInfo+0x08. On
// VRChat build 1903 that slot holds the metadata TOKEN, so every field read lands on unmapped
// memory: an access violation, which .NET cannot catch, so the process simply dies.
//
// Measured, not assumed: for UnityEngine.Object.m_CachedPtr, FieldInfo+0x08 reads 0x9CCAF5F8
// (a truncated pointer, different every launch) while FieldInfo+0x10 reads 0x10 -- the real
// offset. Reading m_CachedPtr there shows a live native pointer, i.e. objects that looked like
// broken shells were valid all along.
//
// The mod carries the same repair, but as a PLUGIN it installs during chainloader Load() -- by
// which time Il2CppInterop has already cached wrong offsets for types touched during startup,
// and those stay wrong. Doing it here, in the PATCHER, fixes it before anything is cached, and
// fixes it for every plugin (UnityExplorer included), not just our mod.
//
// The slot is FOUND, never assumed: System.Delegate's first four fields must report 0x10/0x18/
// 0x20/0x28. If they do not all agree, the patch declines and stock behaviour is left alone --
// a wrong answer here would corrupt every field access in the process.
internal static class FieldOffsetGlobalFix
{
	private static readonly (string Name, uint Offset)[] Known =
		{ ("method_ptr", 0x10u), ("invoke_impl", 0x18u), ("m_target", 0x20u), ("method", 0x28u) };

	private static int _slot = -1;      // -1 unknown, -2 give up
	private static bool _logged;
	private static ManualLogSource _log;

	internal static void Install(ManualLogSource log)
	{
		_log = log;
		try
		{
			var target = typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod(
				"il2cpp_field_get_offset", BindingFlags.Static | BindingFlags.Public);
			if (target == null) { log.LogWarning((object)"VRChatStructFix: IL2CPP.il2cpp_field_get_offset introuvable — offsets de champs non corriges."); return; }
			new Harmony("va.structfix.fieldoffset").Patch(target,
				prefix: new HarmonyMethod(typeof(FieldOffsetGlobalFix).GetMethod(
					nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
			log.LogMessage((object)"VRChatStructFix: correctif d'offsets de champs arme (slot mesure au premier appel).");
		}
		catch (Exception e) { log.LogWarning((object)("VRChatStructFix: correctif d'offsets non installe : " + e.Message)); }
	}

	private unsafe static bool Prefix(IntPtr field, ref uint __result)
	{
		if (_slot == -2) return true;                 // detection failed once: never interfere again
		if (_slot < 0 && !Detect()) return true;
		if (field == IntPtr.Zero) return true;
		__result = *(uint*)((byte*)field + _slot);
		return false;                                 // skip the stock implementation
	}

	private unsafe static bool Detect()
	{
		try
		{
			IntPtr klass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Delegate");
			if (klass == IntPtr.Zero) { _slot = -2; return false; }

			var fields = new IntPtr[Known.Length];
			for (int i = 0; i < Known.Length; i++)
			{
				fields[i] = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, Known[i].Name);
				if (fields[i] == IntPtr.Zero) { _slot = -2; return false; }
			}

			// only a slot where ALL four known offsets appear is accepted
			for (int slot = 0; slot <= 0x28; slot += 4)
			{
				bool all = true;
				for (int i = 0; i < Known.Length && all; i++)
					all = *(uint*)((byte*)fields[i] + slot) == Known[i].Offset;
				if (all)
				{
					_slot = slot;
					if (!_logged && _log != null)
					{
						_logged = true;
						_log.LogMessage((object)("VRChatStructFix: offsets de champs repares — lecture a FieldInfo+0x"
							+ slot.ToString("X") + (slot == 0x08 ? " (identique au stock, sans effet)" : " au lieu de +0x08")));
					}
					return slot != 0x08;              // if stock is already right, do not interfere
				}
			}
			_slot = -2;
			if (_log != null) _log.LogWarning((object)"VRChatStructFix: aucun slot FieldInfo ne porte les offsets attendus — offsets laisses tels quels.");
			return false;
		}
		catch { _slot = -2; return false; }
	}
}

internal static class VrcOffsets
{
	public const int Method_MethodPointer = 0;

	public const int Method_VirtualMethodPointer = 8;

	public const int Method_InvokerMethod = 16;

	public const int Method_Class = 24;

	public const int Method_ReturnType = 32;

	public const int Method_Name = 40;

	public const int Method_Parameters = 48;

	public const int Method_Token = 72;

	public const int Method_Flags = 76;

	public const int Method_Slot = 80;

	public const int Method_ParametersCount = 82;

	public const int Method_Bitfield0 = 83;

	public const int Method_Size = 88;

	public const int MethodBit_IsGeneric = 0;

	public const int MethodBit_IsInflated = 1;

	public const int Class_Image = 0;

	public const int Class_Self = 16;

	public const int Class_Namespace = 24;

	public const int Class_ByValArg = 32;

	public const int Class_ThisArg = 48;

	public const int Class_ElementClass = 64;

	public const int Class_Parent = 120;

	public const int Class_DeclaringType = 88;

	public const int Class_Methods = 160;

	public const int Class_CastClass = 128;

	public const int Class_Fields = 144;

	public const int Class_Name = 88;

	public const int Class_Properties = 160;

	public const int Class_ImplementedInterfaces = 80;

	public const int Class_TypeHierarchy = 200;

	public const int Class_InterfaceOffsets = 176;

	public const int Class_InstanceSize = 248;

	public const int Class_ActualSize = 256;

	public const int Class_NativeSize = 264;

	public const int Class_Flags = 280;

	public const int Class_MethodCount = 288;

	public const int Class_PropertyCount = 290;

	public const int Class_FieldCount = 292;

	public const int Class_EventCount = 294;

	public const int Class_VtableCount = 298;

	public const int Class_InterfacesCount = 300;

	public const int Class_InterfaceOffsetsCount = 302;

	public const int Class_TypeHierarchyDepth = 304;

	public const int Class_Bitfield0 = 309;

	public const int Class_Bitfield1 = 310;

	public const int Class_Size = 312;

	public const int ClassBit0_InitializedAndNoError = 0;

	public const int ClassBit0_Initialized = 1;

	public const int ClassBit0_EnumType = 2;

	public const int ClassBit0_IsGeneric = 4;

	public const int ClassBit1_SizeInited = 0;

	public const int ClassBit1_HasFinalize = 1;

	public const int ClassBit1_IsVtableInitialized = 5;

	public const int Image_TypeCount = 0;

	public const int Image_NameNoExt = 8;

	public const int Image_Dynamic = 16;

	public const int Image_CustomAttributeCount = 20;

	public const int Image_Name = 24;

	public const int Image_Assembly = 32;

	public const int Image_MetadataHandle = 40;

	public const int Image_NameToClassHashTable = 48;

	public const int Image_CodeGenModule = 56;

	public const int Image_Token = 64;

	public const int Image_ExportedTypeCount = 68;

	public const int Image_Size = 72;
}
