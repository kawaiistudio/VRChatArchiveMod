using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRChatArchiveMod.Core;

namespace VRChatArchiveMod.Modules
{
	/// <summary>
	/// Unity MCP Bridge: Embedded in-game local IPC server that allows external AI assistants
	/// (via Model Context Protocol) to inspect live Unity hierarchies, analyze RectTransforms,
	/// examine active UI canvases, read component states, and trigger UI interactions in real time.
	/// </summary>
	public class UnityMcpBridgeModule : IModule
	{
		public override string Name => "UnityMcpBridge";

		public const int Port = 28765;
		private TcpListener _listener;
		private Thread _serverThread;
		private volatile bool _running;

		private sealed class McpRequest
		{
			public string Command;
			public JsonElement Params;
			public TaskCompletionSource<string> Tcs;
		}

		private readonly ConcurrentQueue<McpRequest> _queue = new ConcurrentQueue<McpRequest>();

		public override void OnInitialize()
		{
			StartServer();
		}

		public override void OnShutdown()
		{
			StopServer();
		}

		private void StartServer()
		{
			if (_running) return;
			try
			{
				_listener = new TcpListener(IPAddress.Loopback, Port);
				_listener.Start();
				_running = true;
				_serverThread = new Thread(ServerLoop)
				{
					IsBackground = true,
					Name = "UnityMcpBridge_Server"
				};
				_serverThread.Start();
				VRChatArchiveModPlugin.Logger.LogInfo($"[UnityMcpBridge] Local MCP Bridge listening on 127.0.0.1:{Port}");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[UnityMcpBridge] Failed to bind to port {Port}: {e.Message}");
			}
		}

		private void StopServer()
		{
			_running = false;
			try { _listener?.Stop(); } catch { }
		}

		private void ServerLoop()
		{
			while (_running)
			{
				try
				{
					var client = _listener.AcceptTcpClient();
					ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
				}
				catch
				{
					if (!_running) break;
				}
			}
		}

		private void HandleClient(TcpClient client)
		{
			using (client)
			using (var stream = client.GetStream())
			using (var reader = new StreamReader(stream, Encoding.UTF8))
			using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
			{
				while (_running && client.Connected)
				{
					string line;
					try
					{
						line = reader.ReadLine();
					}
					catch
					{
						break;
					}

					if (string.IsNullOrEmpty(line)) break;

					try
					{
						using var doc = JsonDocument.Parse(line);
						var root = doc.RootElement;
						string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : "ping";
						root.TryGetProperty("params", out var p);

						var tcs = new TaskCompletionSource<string>();
						_queue.Enqueue(new McpRequest
						{
							Command = cmd,
							Params = p.Clone(),
							Tcs = tcs
						});

						// Wait for main thread to execute with 10s timeout
						if (tcs.Task.Wait(10000))
						{
							writer.WriteLine(tcs.Task.Result);
						}
						else
						{
							writer.WriteLine("{\"error\": \"Command timed out on Unity main thread\"}");
						}
					}
					catch (Exception ex)
					{
						writer.WriteLine($"{{\"error\": \"Exception parsing command: {Escape(ex.Message)}\"}}");
					}
				}
			}
		}

		public override void OnUpdate()
		{
			// Process up to 32 pending requests per frame on the main thread
			int count = 0;
			while (_queue.TryDequeue(out var req) && count < 32)
			{
				count++;
				try
				{
					string result = ExecuteCommand(req.Command, req.Params);
					req.Tcs.TrySetResult(result);
				}
				catch (Exception e)
				{
					req.Tcs.TrySetResult($"{{\"error\": \"Execution failed: {Escape(e.Message)}\"}}");
				}
			}
		}

		private string ExecuteCommand(string cmd, JsonElement p)
		{
			switch (cmd?.ToLowerInvariant())
			{
				case "status":
				case "ping":
					return GetStatus();

				case "find_objects":
					return FindObjects(p);

				case "dump_hierarchy":
					return DumpHierarchy(p);

				case "inspect_object":
					return InspectObject(p);

				case "set_active":
					return SetActive(p);

				case "click_button":
					return ClickButton(p);

				case "get_text":
					return GetText(p);

				case "set_text":
					return SetText(p);

				case "get_config":
					return GetConfig(p);

				case "set_config":
					return SetConfig(p);

				case "inspect_components":
					return InspectComponents(p);

				case "inspect_udon":
					return InspectUdon(p);

				case "get_roster":
					return GetRoster(p);

				case "freeze_player":
					return FreezePlayerCmd(p);

				default:
					return $"{{\"error\": \"Unknown command '{Escape(cmd)}'\"}}";
			}
		}

		private string GetStatus()
		{
			float fps = 1f / Mathf.Max(0.0001f, VaClock.Delta);
			string scene = SceneManager.GetActiveScene().name;
			bool mainOpen = false;
			bool qmOpen = false;

			try
			{
				var main = Core.QuickMenu.Main();
				if (main != null) mainOpen = main.gameObject.activeInHierarchy;
				var qm = Core.QuickMenu.Root();
				if (qm != null) qmOpen = qm.gameObject.activeInHierarchy;
			}
			catch { }

			var sb = new StringBuilder();
			sb.Append("{");
			sb.Append("\"status\":\"connected\",");
			sb.Append($"\"unity_version\":\"{Escape(Application.unityVersion)}\",");
			sb.Append($"\"product_name\":\"{Escape(Application.productName)}\",");
			sb.Append($"\"scene\":\"{Escape(scene)}\",");
			sb.Append($"\"fps\":{fps:F1},");
			sb.Append($"\"main_menu_open\":{(mainOpen ? "true" : "false")},");
			sb.Append($"\"quick_menu_open\":{(qmOpen ? "true" : "false")}");
			sb.Append("}");
			return sb.ToString();
		}

		private string FindObjects(JsonElement p)
		{
			string query = p.TryGetProperty("query", out var q) ? q.GetString() : "";
			bool activeOnly = p.TryGetProperty("active_only", out var a) && a.GetBoolean();
			int max = p.TryGetProperty("max", out var m) ? m.GetInt32() : 25;
			if (max <= 0 || max > 100) max = 25;

			var results = new List<string>();
			var all = Resources.FindObjectsOfTypeAll<GameObject>();

			foreach (var go in all)
			{
				if (go == null) continue;
				if (activeOnly && !go.activeInHierarchy) continue;
				if (!string.IsNullOrEmpty(query) && go.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

				string path = GetHierarchyPath(go.transform);
				results.Add($"{{\"name\":\"{Escape(go.name)}\",\"path\":\"{Escape(path)}\",\"active\":{(go.activeInHierarchy ? "true" : "false")}}}");
				if (results.Count >= max) break;
			}

			return $"{{\"query\":\"{Escape(query)}\",\"count\":{results.Count},\"results\":[{string.Join(",", results)}]}}";
		}

		private string DumpHierarchy(JsonElement p)
		{
			string rootQuery = p.TryGetProperty("path", out var pt) ? pt.GetString() : "Canvas_MainMenu(Clone)";
			int maxDepth = p.TryGetProperty("depth", out var d) ? d.GetInt32() : 3;
			if (maxDepth <= 0 || maxDepth > 8) maxDepth = 3;

			Transform target = ResolveTransform(rootQuery);
			if (target == null)
			{
				return $"{{\"error\":\"Root object '{Escape(rootQuery)}' not found in scene\"}}";
			}

			var sb = new StringBuilder();
			DumpTransformRecursive(target, 0, maxDepth, sb);
			return sb.ToString();
		}

		private void DumpTransformRecursive(Transform t, int depth, int maxDepth, StringBuilder sb)
		{
			if (t == null) return;
			var rt = t.GetComponent<RectTransform>();

			sb.Append("{");
			sb.Append($"\"name\":\"{Escape(t.name)}\",");
			sb.Append($"\"active\":{(t.gameObject.activeSelf ? "true" : "false")},");
			sb.Append($"\"active_in_hierarchy\":{(t.gameObject.activeInHierarchy ? "true" : "false")}");

			if (rt != null)
			{
				sb.Append($",\"rect\":{{\"w\":{rt.rect.width:F0},\"h\":{rt.rect.height:F0}}}");
			}

			// Component names
			var comps = t.GetComponents<Component>();
			var compNames = new List<string>();
			if (comps != null)
			{
				foreach (var c in comps)
				{
					if (c != null) compNames.Add($"\"{Escape(c.GetIl2CppType().Name)}\"");
				}
			}
			sb.Append($",\"components\":[{string.Join(",", compNames)}]");

			if (depth < maxDepth && t.childCount > 0)
			{
				sb.Append(",\"children\":[");
				int count = Mathf.Min(t.childCount, 30);
				for (int i = 0; i < count; i++)
				{
					if (i > 0) sb.Append(",");
					DumpTransformRecursive(t.GetChild(i), depth + 1, maxDepth, sb);
				}
				sb.Append("]");
			}

			sb.Append("}");
		}

		private string InspectObject(JsonElement p)
		{
			string path = p.TryGetProperty("path", out var pt) ? pt.GetString() : "";
			Transform target = ResolveTransform(path);
			if (target == null)
			{
				return $"{{\"error\":\"Object '{Escape(path)}' not found\"}}";
			}

			var sb = new StringBuilder();
			sb.Append("{");
			sb.Append($"\"name\":\"{Escape(target.name)}\",");
			sb.Append($"\"path\":\"{Escape(GetHierarchyPath(target))}\",");
			sb.Append($"\"active_self\":{(target.gameObject.activeSelf ? "true" : "false")},");
			sb.Append($"\"active_in_hierarchy\":{(target.gameObject.activeInHierarchy ? "true" : "false")},");
			sb.Append($"\"layer\":{target.gameObject.layer},");
			sb.Append($"\"tag\":\"{Escape(target.tag)}\",");
			sb.Append($"\"child_count\":{target.childCount},");
			sb.Append($"\"world_position\":[{target.position.x:F3},{target.position.y:F3},{target.position.z:F3}],");
			sb.Append($"\"local_position\":[{target.localPosition.x:F3},{target.localPosition.y:F3},{target.localPosition.z:F3}],");
			sb.Append($"\"local_scale\":[{target.localScale.x:F4},{target.localScale.y:F4},{target.localScale.z:F4}],");
			sb.Append($"\"lossy_scale\":[{target.lossyScale.x:F4},{target.lossyScale.y:F4},{target.lossyScale.z:F4}],");
			sb.Append($"\"world_rotation\":[{target.eulerAngles.x:F1},{target.eulerAngles.y:F1},{target.eulerAngles.z:F1}]");

			var rt = target.GetComponent<RectTransform>();
			if (rt != null)
			{
				sb.Append($",\"rect_transform\":{{");
				sb.Append($"\"anchored_pos\":[{rt.anchoredPosition.x:F1},{rt.anchoredPosition.y:F1}],");
				sb.Append($"\"size_delta\":[{rt.sizeDelta.x:F1},{rt.sizeDelta.y:F1}],");
				sb.Append($"\"pivot\":[{rt.pivot.x:F2},{rt.pivot.y:F2}],");
				sb.Append($"\"rect\":{{\"x\":{rt.rect.x:F1},\"y\":{rt.rect.y:F1},\"w\":{rt.rect.width:F1},\"h\":{rt.rect.height:F1}}}");
				sb.Append("}");
			}

			// Details on key UI components
			var tmp = target.GetComponent<TMPro.TMP_Text>() ?? target.GetComponentInChildren<TMPro.TMP_Text>(true);
			if (tmp != null)
			{
				sb.Append($",\"text_component\":{{\"text\":\"{Escape(tmp.text)}\",\"font_size\":{tmp.fontSize:F1},\"enabled\":{(tmp.enabled ? "true" : "false")}}}");
			}

			var img = target.GetComponent<Image>();
			if (img != null)
			{
				string spName = (img.sprite != null) ? img.sprite.name : "null";
				sb.Append($",\"image_component\":{{\"sprite\":\"{Escape(spName)}\",\"raycast_target\":{(img.raycastTarget ? "true" : "false")},\"enabled\":{(img.enabled ? "true" : "false")}}}");
			}

			var btn = target.GetComponent<Button>();
			if (btn != null)
			{
				sb.Append($",\"button_component\":{{\"interactable\":{(btn.interactable ? "true" : "false")},\"enabled\":{(btn.enabled ? "true" : "false")}}}");
			}

			var tog = target.GetComponent<Toggle>();
			if (tog != null)
			{
				sb.Append($",\"toggle_component\":{{\"is_on\":{(tog.isOn ? "true" : "false")},\"interactable\":{(tog.interactable ? "true" : "false")},\"enabled\":{(tog.enabled ? "true" : "false")}}}");
			}

			var le = target.GetComponent<LayoutElement>();
			if (le != null)
			{
				sb.Append($",\"layout_element\":{{\"min_w\":{le.minWidth:F0},\"pref_w\":{le.preferredWidth:F0},\"flex_w\":{le.flexibleWidth:F0},\"min_h\":{le.minHeight:F0},\"pref_h\":{le.preferredHeight:F0}}}");
			}

			var cr = target.GetComponent<CanvasRenderer>();
			if (cr != null && cr.materialCount > 0)
			{
				var m = cr.GetMaterial(0);
				if (m != null)
				{
					sb.Append($",\"material_name\":\"{Escape(m.name)}\"");
					if (m.shader != null)
						sb.Append($",\"shader_name\":\"{Escape(m.shader.name)}\"");
					sb.Append($",\"has_GUIZTest\":{(m.HasProperty("unity_GUIZTestMode") ? "true" : "false")}");
					sb.Append($",\"has_ZTestMode\":{(m.HasProperty("_ZTestMode") ? "true" : "false")}");
					sb.Append($",\"has_ZTest\":{(m.HasProperty("_ZTest") ? "true" : "false")}");
					if (m.HasProperty("unity_GUIZTestMode")) sb.Append($",\"GUIZTest\":{m.GetInt("unity_GUIZTestMode")}");
					if (m.HasProperty("_ZTestMode")) sb.Append($",\"ZTestMode\":{m.GetInt("_ZTestMode")}");
					if (m.HasProperty("_ZTest")) sb.Append($",\"ZTest\":{m.GetInt("_ZTest")}");
				}
			}

			var gr = target.GetComponent<UnityEngine.UI.Graphic>();
			if (gr != null)
			{
				var m = gr.material;
				if (m != null)
				{
					sb.Append($",\"graphic_material\":\"{Escape(m.name)}\"");
					if (m.shader != null) sb.Append($",\"graphic_shader\":\"{Escape(m.shader.name)}\"");
				}
			}

			if (target.childCount > 0)
			{
				sb.Append(",\"children\":[");
				for (int i = 0; i < target.childCount; i++)
				{
					if (i > 0) sb.Append(",");
					var child = target.GetChild(i);
					sb.Append($"\"{Escape(child.name)}\"");
				}
				sb.Append("]");
			}

			sb.Append("}");
			return sb.ToString();
		}

		private string SetActive(JsonElement p)
		{
			string path = p.TryGetProperty("path", out var pt) ? pt.GetString() : "";
			bool active = p.TryGetProperty("active", out var a) && a.GetBoolean();

			Transform target = ResolveTransform(path);
			if (target == null)
			{
				return $"{{\"error\":\"Object '{Escape(path)}' not found\"}}";
			}

			target.gameObject.SetActive(active);
			return $"{{\"success\":true,\"path\":\"{Escape(GetHierarchyPath(target))}\",\"active\":{target.gameObject.activeSelf}}}";
		}

		private string ClickButton(JsonElement p)
		{
			string path = p.TryGetProperty("path", out var pt) ? pt.GetString() : "";
			Transform target = ResolveTransform(path);
			if (target == null)
			{
				return $"{{\"error\":\"Object '{Escape(path)}' not found\"}}";
			}

			var btn = target.GetComponent<Button>() ?? target.GetComponentInChildren<Button>(true);
			if (btn != null)
			{
				try { btn.Press(); } catch { }
				try { btn.onClick?.Invoke(); } catch { }
				return $"{{\"success\":true,\"path\":\"{Escape(GetHierarchyPath(target))}\",\"clicked\":true,\"type\":\"Button\"}}";
			}

			var tog = target.GetComponent<Toggle>() ?? target.GetComponentInChildren<Toggle>(true);
			if (tog != null)
			{
				try
				{
					tog.isOn = !tog.isOn;
				}
				catch { }
				return $"{{\"success\":true,\"path\":\"{Escape(GetHierarchyPath(target))}\",\"toggled\":true,\"is_on\":{(tog.isOn ? "true" : "false")},\"type\":\"Toggle\"}}";
			}

			return $"{{\"error\":\"No Button or Toggle component found on '{Escape(path)}'\"}}";
		}

		private string GetConfig(JsonElement p)
		{
			string section = p.TryGetProperty("section", out var s) ? s.GetString() : "";
			string key = p.TryGetProperty("key", out var k) ? k.GetString() : "";

			if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
			{
				return "{\"error\":\"Missing section or key parameter\"}";
			}

			try
			{
				var cfg = ModConfig.ConfigFile;
				if (cfg != null)
				{
					var entry = cfg[section, key];
					if (entry != null)
					{
						return $"{{\"section\":\"{Escape(section)}\",\"key\":\"{Escape(key)}\",\"value\":\"{Escape(entry.BoxedValue?.ToString() ?? "")}\"}}";
					}
				}
				return $"{{\"error\":\"Config entry '{Escape(section)}/{Escape(key)}' not found\"}}";
			}
			catch (Exception e)
			{
				return $"{{\"error\":\"Failed to get config: {Escape(e.Message)}\"}}";
			}
		}

		private string SetConfig(JsonElement p)
		{
			string section = p.TryGetProperty("section", out var s) ? s.GetString() : "";
			string key = p.TryGetProperty("key", out var k) ? k.GetString() : "";
			string val = p.TryGetProperty("value", out var v) ? v.GetString() : "";

			if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
			{
				return "{\"error\":\"Missing section or key parameter\"}";
			}

			try
			{
				var cfg = ModConfig.ConfigFile;
				if (cfg != null)
				{
					var entry = cfg[section, key];
					if (entry != null)
					{
						if (entry.SettingType == typeof(bool) && bool.TryParse(val, out bool bVal))
						{
							entry.BoxedValue = bVal;
						}
						else if (entry.SettingType == typeof(float) && float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fVal))
						{
							entry.BoxedValue = fVal;
						}
						else if (entry.SettingType == typeof(int) && int.TryParse(val, out int iVal))
						{
							entry.BoxedValue = iVal;
						}
						else
						{
							entry.BoxedValue = val;
						}
						return $"{{\"success\":true,\"section\":\"{Escape(section)}\",\"key\":\"{Escape(key)}\",\"new_value\":\"{Escape(entry.BoxedValue?.ToString() ?? "")}\"}}";
					}
				}
				return $"{{\"error\":\"Config entry '{Escape(section)}/{Escape(key)}' not found\"}}";
			}
			catch (Exception e)
			{
				return $"{{\"error\":\"Failed to set config: {Escape(e.Message)}\"}}";
			}
		}

		private string GetText(JsonElement p)
		{
			string path = p.TryGetProperty("path", out var pt) ? pt.GetString() : "";
			Transform target = ResolveTransform(path);
			if (target == null) return $"{{\"error\":\"Object '{Escape(path)}' not found\"}}";

			var tmp = target.GetComponent<TMPro.TMP_Text>() ?? target.GetComponentInChildren<TMPro.TMP_Text>(true);
			if (tmp != null)
			{
				return $"{{\"success\":true,\"text\":\"{Escape(tmp.text)}\"}}";
			}

			var txt = target.GetComponent<Text>() ?? target.GetComponentInChildren<Text>(true);
			if (txt != null)
			{
				return $"{{\"success\":true,\"text\":\"{Escape(txt.text)}\"}}";
			}

			return "{\"error\":\"No TMP_Text or Text component found\"}";
		}

		private string SetText(JsonElement p)
		{
			string path = p.TryGetProperty("path", out var pt) ? pt.GetString() : "";
			string newText = p.TryGetProperty("text", out var t) ? t.GetString() : "";
			Transform target = ResolveTransform(path);
			if (target == null) return $"{{\"error\":\"Object '{Escape(path)}' not found\"}}";

			var tmp = target.GetComponent<TMPro.TMP_Text>() ?? target.GetComponentInChildren<TMPro.TMP_Text>(true);
			if (tmp != null)
			{
				tmp.text = newText;
				return $"{{\"success\":true,\"text\":\"{Escape(tmp.text)}\"}}";
			}

			var txt = target.GetComponent<Text>() ?? target.GetComponentInChildren<Text>(true);
			if (txt != null)
			{
				txt.text = newText;
				return $"{{\"success\":true,\"text\":\"{Escape(txt.text)}\"}}";
			}

			return "{\"error\":\"No TMP_Text or Text component found\"}";
		}

		private string InspectComponents(JsonElement p)
		{
			string path = p.TryGetProperty("path", out var pt) ? pt.GetString() : "";
			Transform target = ResolveTransform(path);
			if (target == null)
			{
				return $"{{\"error\":\"Object '{Escape(path)}' not found\"}}";
			}

			var sb = new StringBuilder();
			sb.Append("{");
			sb.Append($"\"path\":\"{Escape(GetHierarchyPath(target))}\",");
			sb.Append("\"components\":[");

			var comps = target.GetComponents<Component>();
			bool first = true;
			for (int i = 0; i < comps.Length; i++)
			{
				var c = comps[i];
				if (c == null) continue;
				if (!first) sb.Append(",");
				first = false;

				sb.Append("{");
				string il2cppName = "";
				try { il2cppName = MenuCard.Il2CppNameOf(c); } catch { }
				string typeName = c.GetType().FullName;
				sb.Append($"\"type\":\"{Escape(typeName)}\",");
				sb.Append($"\"il2cpp_type\":\"{Escape(il2cppName)}\"");

				var ub = c.TryCast<VRC.Udon.UdonBehaviour>();
				if (ub != null)
				{
					sb.Append(",\"is_udon\":true");
					try
					{
						var eps = Core.UdonSymbols.Exported(ub);
						sb.Append(",\"entry_points\":[");
						for (int k = 0; k < eps.Count; k++)
						{
							if (k > 0) sb.Append(",");
							sb.Append($"\"{Escape(eps[k])}\"");
						}
						sb.Append("]");
					}
					catch { }
				}

				var btn = c.TryCast<Button>();
				if (btn != null)
				{
					sb.Append(",\"is_button\":true");
					sb.Append($",\"interactable\":{(btn.interactable ? "true" : "false")}");
					try
					{
						int n = btn.onClick.GetPersistentEventCount();
						sb.Append(",\"listeners\":[");
						for (int k = 0; k < n; k++)
						{
							if (k > 0) sb.Append(",");
							var t = btn.onClick.GetPersistentTarget(k);
							string m = btn.onClick.GetPersistentMethodName(k);
							string tName = t != null ? t.name : "null";
							sb.Append($"{{\"target\":\"{Escape(tName)}\",\"method\":\"{Escape(m)}\"}}");
						}
						sb.Append("]");
					}
					catch { }
				}

				var tog = c.TryCast<Toggle>();
				if (tog != null)
				{
					sb.Append(",\"is_toggle\":true");
					sb.Append($",\"is_on\":{(tog.isOn ? "true" : "false")}");
					sb.Append($",\"interactable\":{(tog.interactable ? "true" : "false")}");
					try
					{
						int n = tog.onValueChanged.GetPersistentEventCount();
						sb.Append(",\"listeners\":[");
						for (int k = 0; k < n; k++)
						{
							if (k > 0) sb.Append(",");
							var t = tog.onValueChanged.GetPersistentTarget(k);
							string m = tog.onValueChanged.GetPersistentMethodName(k);
							string tName = t != null ? t.name : "null";
							sb.Append($"{{\"target\":\"{Escape(tName)}\",\"method\":\"{Escape(m)}\"}}");
						}
						sb.Append("]");
					}
					catch { }
				}

				sb.Append("}");
			}
			sb.Append("]}");
			return sb.ToString();
		}

		private string InspectUdon(JsonElement p)
		{
			string query = p.TryGetProperty("path", out var pt) ? pt.GetString()
			             : (p.TryGetProperty("query", out var q) ? q.GetString() : "");
			if (string.IsNullOrEmpty(query)) return "{\"error\":\"path is required\"}";

			var target = ResolveTransform(query);
			if (target == null) return $"{{\"error\": \"Object not found: {Escape(query)}\"}}";

			var ub = target.GetComponent<VRC.Udon.UdonBehaviour>()
			      ?? target.GetComponentInParent<VRC.Udon.UdonBehaviour>()
			      ?? target.GetComponentInChildren<VRC.Udon.UdonBehaviour>(true);

			var sb = new StringBuilder();
			sb.Append("{");
			sb.Append($"\"object\":\"{Escape(target.name)}\",");
			sb.Append($"\"path\":\"{Escape(GetHierarchyPath(target))}\",");

			if (ub == null)
			{
				sb.Append("\"has_udon\":false");
			}
			else
			{
				sb.Append("\"has_udon\":true,");
				sb.Append($"\"udon_path\":\"{Escape(GetHierarchyPath(ub.transform))}\",");

				// Check SyncMethod property
				try
				{
					var prop = ub.GetType().GetProperty("SyncMethod");
					if (prop != null)
					{
						object val = prop.GetValue(ub);
						sb.Append($"\"sync_method\":\"{Escape(val?.ToString())}\",");
						sb.Append($"\"sync_can_write\":{(prop.CanWrite ? "true" : "false")},");
					}
					else
					{
						sb.Append("\"sync_method\":\"property_not_found\",");
					}
				}
				catch (Exception ex)
				{
					sb.Append($"\"sync_method_error\":\"{Escape(ex.Message)}\",");
				}

				// Check GetPrograms()
				try
				{
					Type ubType = ub.GetType();
					var mi = ubType.GetMethod("GetPrograms");
					object arr = mi?.Invoke(ub, null);
					sb.Append("\"programs\":[");
					if (arr != null)
					{
						var at = arr.GetType();
						var lenP = at.GetProperty("Length") ?? at.GetProperty("Count");
						var item = at.GetProperty("Item");
						if (lenP?.GetValue(arr) is int len && item != null)
						{
							for (int i = 0; i < len && i < 100; i++)
							{
								if (i > 0) sb.Append(",");
								string n = item.GetValue(arr, new object[] { i }) as string;
								sb.Append($"\"{Escape(n)}\"");
							}
						}
					}
					sb.Append("],");
				}
				catch (Exception ex)
				{
					sb.Append($"\"programs_error\":\"{Escape(ex.Message)}\",");
				}

				// Check public properties and fields on UdonBehaviour safely (avoid native IL2CPP property crashes)
				sb.Append("\"udon_properties\":[");
				try
				{
					sb.Append($"{{\"name\":\"name\",\"type\":\"String\",\"val\":\"{Escape(ub.name)}\"}},");
					sb.Append($"{{\"name\":\"IsNetworkingSupported\",\"type\":\"Boolean\",\"val\":\"{ub.IsNetworkingSupported}\"}},");
					sb.Append($"{{\"name\":\"SyncMethod\",\"type\":\"String\",\"val\":\"{Escape(ub.SyncMethod.ToString())}\"}}");
				}
				catch { }
				sb.Append("],");

				// Check button listener details
				var btn = target.GetComponent<Button>() ?? target.GetComponentInChildren<Button>(true);
				if (btn != null)
				{
					sb.Append("\"button\":{");
					sb.Append($"\"interactable\":{(btn.interactable ? "true" : "false")},");
					sb.Append("\"listeners\":[");
					try
					{
						int cnt = btn.onClick.GetPersistentEventCount();
						for (int i = 0; i < cnt; i++)
						{
							if (i > 0) sb.Append(",");
							var tg = btn.onClick.GetPersistentTarget(i);
							string m = btn.onClick.GetPersistentMethodName(i);
							string tgName = tg != null ? tg.name : "null";
							sb.Append($"{{\"target\":\"{Escape(tgName)}\",\"method\":\"{Escape(m)}\"}}");
						}
					}
					catch { }
					sb.Append("]},");
				}

				sb.Append("\"status\":\"ok\"");
			}

			sb.Append("}");
			return sb.ToString();
		}

		private static Transform ResolveTransform(string query)
		{
			if (string.IsNullOrEmpty(query)) return null;

			// Direct path search
			var direct = GameObject.Find(query);
			if (direct != null) return direct.transform;

			// Path walking (handles inactive objects via Transform.Find)
			if (query.Contains("/"))
			{
				var parts = query.Split('/');
				Transform cur = null;
				// Find root in scenes
				for (int i = 0; i < SceneManager.sceneCount; i++)
				{
					var sc = SceneManager.GetSceneAt(i);
					if (!sc.isLoaded) continue;
					foreach (var root in sc.GetRootGameObjects())
					{
						if (root != null && string.Equals(root.name, parts[0], StringComparison.OrdinalIgnoreCase))
						{
							cur = root.transform;
							break;
						}
					}
					if (cur != null) break;
				}

				if (cur == null)
				{
					foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
					{
						if (go != null && string.Equals(go.name, parts[0], StringComparison.OrdinalIgnoreCase))
						{
							cur = go.transform;
							break;
						}
					}
				}

				if (cur != null)
				{
					for (int i = 1; i < parts.Length; i++)
					{
						cur = cur.Find(parts[i]);
						if (cur == null) break;
					}
					if (cur != null) return cur;
				}
			}

			// Check all root objects in loaded scenes
			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				var sc = SceneManager.GetSceneAt(i);
				if (!sc.isLoaded) continue;
				foreach (var root in sc.GetRootGameObjects())
				{
					if (root == null) continue;
					if (string.Equals(root.name, query, StringComparison.OrdinalIgnoreCase)) return root.transform;
					var found = FindDescendant(root.transform, query);
					if (found != null) return found;
				}
			}

			// Fallback: search all objects in memory
			foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
			{
				if (go != null && string.Equals(go.name, query, StringComparison.OrdinalIgnoreCase))
					return go.transform;
			}

			return null;
		}

		private static Transform FindDescendant(Transform t, string query)
		{
			if (t == null) return null;
			for (int i = 0; i < t.childCount; i++)
			{
				var c = t.GetChild(i);
				if (c == null) continue;
				if (string.Equals(c.name, query, StringComparison.OrdinalIgnoreCase)) return c;
				var sub = FindDescendant(c, query);
				if (sub != null) return sub;
			}
			return null;
		}

		private static string GetHierarchyPath(Transform t)
		{
			if (t == null) return "";
			var path = new List<string>();
			var cur = t;
			while (cur != null)
			{
				path.Add(cur.name);
				cur = cur.parent;
			}
			path.Reverse();
			return string.Join("/", path);
		}

		private static string Escape(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Replace("\\", "\\\\")
			        .Replace("\"", "\\\"")
			        .Replace("\n", "\\n")
			        .Replace("\r", "\\r")
			        .Replace("\t", "\\t");
		}

		private string GetRoster(JsonElement p)
		{
			var sb = new StringBuilder();
			sb.Append("{\"players\":[");
			bool first = true;
			lock (VaTagsModule.Roster)
			{
				foreach (var entry in VaTagsModule.Roster)
				{
					if (entry == null) continue;
					if (!first) sb.Append(",");
					first = false;

					Vector3 pos = entry.Pos;
					string goName = "";
					try
					{
						if (entry.Transform != null && NativeGuard.Alive(entry.Transform))
						{
							pos = entry.Transform.position;
							var go = entry.Transform.gameObject;
							if (go != null && NativeGuard.Alive(go)) goName = go.name;
						}
					}
					catch { }

					bool frozen = PlayerFreezeModule.IsUserFrozen(entry.UserId);

					sb.Append("{");
					sb.Append($"\"name\":\"{Escape(entry.Name)}\",");
					sb.Append($"\"user_id\":\"{Escape(entry.UserId)}\",");
					sb.Append($"\"actor_id\":{entry.PlayerId},");
					sb.Append($"\"is_local\":{(entry.IsLocal ? "true" : "false")},");
					sb.Append($"\"is_master\":{(entry.IsMaster ? "true" : "false")},");
					sb.Append($"\"is_frozen\":{(frozen ? "true" : "false")},");
					sb.Append($"\"go_name\":\"{Escape(goName)}\",");
					sb.Append($"\"position\":[{pos.x:F3},{pos.y:F3},{pos.z:F3}]");
					sb.Append("}");
				}
			}
			sb.Append("]}");
			return sb.ToString();
		}

		private string FreezePlayerCmd(JsonElement p)
		{
			string target = p.TryGetProperty("target", out var tg) ? tg.GetString() : "";
			string action = p.TryGetProperty("action", out var ac) ? ac.GetString()?.ToLowerInvariant() : "freeze";
			bool dropNet = !p.TryGetProperty("drop_net", out var dn) || dn.GetBoolean();
			bool pauseAnim = !p.TryGetProperty("pause_anim", out var pa) || pa.GetBoolean();
			bool pinTr = !p.TryGetProperty("pin_tr", out var pt) || pt.GetBoolean();

			if (string.IsNullOrEmpty(target))
			{
				return "{\"error\":\"Missing 'target' parameter (player name, userId, actorId, or GO name)\"}";
			}

			VaTagsModule.PlayerEntry matched = null;
			lock (VaTagsModule.Roster)
			{
				foreach (var e in VaTagsModule.Roster)
				{
					if (e == null) continue;
					if (string.Equals(e.Name, target, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(e.UserId, target, StringComparison.OrdinalIgnoreCase)
						|| (e.Transform != null && e.Transform.gameObject.name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
						|| (int.TryParse(target, out int aid) && e.PlayerId == aid))
					{
						matched = e;
						break;
					}
				}
			}

			if (matched == null)
			{
				return $"{{\"error\":\"Player '{Escape(target)}' not found in Roster\"}}";
			}

			string uid = matched.UserId;
			if (string.IsNullOrEmpty(uid)) uid = matched.Name;

			if (action == "status")
			{
				bool isF = PlayerFreezeModule.IsUserFrozen(uid);
				return $"{{\"name\":\"{Escape(matched.Name)}\",\"user_id\":\"{Escape(matched.UserId)}\",\"actor_id\":{matched.PlayerId},\"is_frozen\":{(isF ? "true" : "false")}}}";
			}
			else if (action == "unfreeze")
			{
				bool ok = PlayerFreezeModule.Unfreeze(uid);
				return $"{{\"success\":true,\"action\":\"unfreeze\",\"name\":\"{Escape(matched.Name)}\",\"unfrozen\":{ok}}}";
			}
			else if (action == "freeze")
			{
				bool ok = PlayerFreezeModule.Freeze(matched, dropNet, pauseAnim, pinTr);
				return $"{{\"success\":true,\"action\":\"freeze\",\"name\":\"{Escape(matched.Name)}\",\"frozen\":{ok},\"actor_id\":{matched.PlayerId},\"pos\":[{matched.Pos.x:F3},{matched.Pos.y:F3},{matched.Pos.z:F3}]}}";
			}
			else if (action == "toggle")
			{
				bool wasF = PlayerFreezeModule.IsUserFrozen(uid);
				PlayerFreezeModule.Toggle(matched);
				bool nowF = PlayerFreezeModule.IsUserFrozen(uid);
				return $"{{\"success\":true,\"action\":\"toggle\",\"name\":\"{Escape(matched.Name)}\",\"was_frozen\":{wasF},\"is_frozen\":{nowF}}}";
			}

			return $"{{\"error\":\"Unknown action '{Escape(action)}'\"}}";
		}
	}
}
