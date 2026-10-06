using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VRChatArchiveMod.Core
{
	/// <summary>
	/// Reads and writes Local Favorite Avatars directly from/to VRCX's local SQLite database (%APPDATA%\VRCX\VRCX.sqlite3)
	/// using Windows native winsqlite3.dll via P/Invoke.
	/// </summary>
	internal static class VrcxLocalFavorites
	{
		private const int SQLITE_OK = 0;
		private const int SQLITE_ROW = 100;
		private const int SQLITE_DONE = 101;
		private const int SQLITE_OPEN_READONLY = 0x00000001;
		private const int SQLITE_OPEN_READWRITE = 0x00000002;
		private const int SQLITE_OPEN_URI = 0x00000040;

		[DllImport("winsqlite3", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
		private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

		[DllImport("winsqlite3", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
		private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

		[DllImport("winsqlite3", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
		private static extern int sqlite3_step(IntPtr pStmt);

		[DllImport("winsqlite3", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
		private static extern IntPtr sqlite3_column_text(IntPtr pStmt, int iCol);

		[DllImport("winsqlite3", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
		private static extern int sqlite3_finalize(IntPtr pStmt);

		[DllImport("winsqlite3", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
		private static extern int sqlite3_close(IntPtr db);

		[DllImport("winsqlite3", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
		private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

		public sealed class VrcxAvatar
		{
			public string Id;
			public string GroupName;
			public string Name;
			public string AuthorName;
			public string ThumbnailUrl;
			public string ImageUrl;
		}

		private static string _dbPath;
		private static long _lastStamp;

		public static string DbPath
		{
			get
			{
				if (_dbPath == null)
				{
					string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
					_dbPath = Path.Combine(appData, "VRCX", "VRCX.sqlite3");
				}
				return _dbPath;
			}
		}

		public static bool Exists => File.Exists(DbPath);

		private static long CalcStamp()
		{
			long s = 0;
			try
			{
				string wal = DbPath + "-wal";
				if (File.Exists(wal))
				{
					var fi = new FileInfo(wal);
					s ^= fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 16);
				}
				if (File.Exists(DbPath))
				{
					var fi = new FileInfo(DbPath);
					s ^= fi.LastWriteTimeUtc.Ticks ^ (fi.Length << 8);
				}
			}
			catch { }
			return s;
		}

		public static bool HasChanged()
		{
			try
			{
				if (!Exists) return false;
				long s = CalcStamp();
				if (_lastStamp == 0)
				{
					_lastStamp = s;
					return false;
				}
				if (s != _lastStamp)
				{
					_lastStamp = s;
					return true;
				}
			}
			catch { }
			return false;
		}

		private static string ReadUtf8(IntPtr ptr)
		{
			if (ptr == IntPtr.Zero) return "";
			return Marshal.PtrToStringUTF8(ptr) ?? "";
		}

		private static string SqlEscape(string s) => (s ?? "").Replace("'", "''");

		private static int ExecuteNonQuery(IntPtr db, string sql)
		{
			if (db == IntPtr.Zero || string.IsNullOrWhiteSpace(sql)) return SQLITE_OK;
			string[] statements = sql.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			int finalRc = SQLITE_OK;
			foreach (var stmtText in statements)
			{
				string trimmed = stmtText.Trim();
				if (string.IsNullOrEmpty(trimmed)) continue;
				IntPtr stmt = IntPtr.Zero;
				try
				{
					byte[] sqlBytes = Encoding.UTF8.GetBytes(trimmed + "\0");
					int rc = sqlite3_prepare_v2(db, sqlBytes, -1, out stmt, IntPtr.Zero);
					if (rc != SQLITE_OK || stmt == IntPtr.Zero)
					{
						VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] sqlite3_prepare_v2 failed (rc={rc}): {trimmed}");
						return rc;
					}
					rc = sqlite3_step(stmt);
					if (rc != SQLITE_DONE && rc != SQLITE_ROW && rc != SQLITE_OK)
					{
						VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] sqlite3_step failed (rc={rc}): {trimmed}");
						return rc;
					}
					finalRc = SQLITE_OK;
				}
				finally
				{
					if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
				}
			}
			return finalRc;
		}

		/// <summary>
		/// Queries favorite_avatar and joins with cache_avatar to get all local favorite avatars with metadata.
		/// </summary>
		public static List<VrcxAvatar> LoadAll()
		{
			var list = new List<VrcxAvatar>();
			if (!Exists) return list;

			IntPtr db = IntPtr.Zero;
			IntPtr stmt = IntPtr.Zero;
			try
			{
				byte[] pathBytes = Encoding.UTF8.GetBytes(DbPath + "\0");
				int rc = sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero);
				if (rc != SQLITE_OK || db == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] sqlite3_open_v2 failed: rc={rc}");
					return list;
				}
				sqlite3_busy_timeout(db, 3000);

				string sql = @"
					SELECT f.avatar_id, f.group_name, c.name, c.author_name, c.thumbnail_image_url, c.image_url 
					FROM favorite_avatar f 
					LEFT JOIN cache_avatar c ON f.avatar_id = c.id 
					ORDER BY f.id DESC;";

				byte[] sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
				rc = sqlite3_prepare_v2(db, sqlBytes, -1, out stmt, IntPtr.Zero);
				if (rc != SQLITE_OK || stmt == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] sqlite3_prepare_v2 failed: rc={rc}");
					return list;
				}

				while (sqlite3_step(stmt) == SQLITE_ROW)
				{
					string avtrId = ReadUtf8(sqlite3_column_text(stmt, 0));
					string group = ReadUtf8(sqlite3_column_text(stmt, 1));
					string name = ReadUtf8(sqlite3_column_text(stmt, 2));
					string author = ReadUtf8(sqlite3_column_text(stmt, 3));
					string thumb = ReadUtf8(sqlite3_column_text(stmt, 4));
					string img = ReadUtf8(sqlite3_column_text(stmt, 5));

					if (!string.IsNullOrEmpty(avtrId) && avtrId.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase))
					{
						list.Add(new VrcxAvatar
						{
							Id = avtrId,
							GroupName = string.IsNullOrWhiteSpace(group) ? "Favorites" : group,
							Name = !string.IsNullOrWhiteSpace(name) ? name : avtrId,
							AuthorName = author ?? "",
							ThumbnailUrl = thumb ?? "",
							ImageUrl = img ?? ""
						});
					}
				}

				_lastStamp = CalcStamp();
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] LoadAll threw: {ex.Message}");
			}
			finally
			{
				if (stmt != IntPtr.Zero) { try { sqlite3_finalize(stmt); } catch { } }
				if (db != IntPtr.Zero) { try { sqlite3_close(db); } catch { } }
			}

			return list;
		}

		/// <summary>
		/// Adds an avatar to VRCX local favorite_avatar and cache_avatar tables.
		/// </summary>
		public static bool AddFavorite(string avatarId, string name, string author, string imageUrl, string thumbUrl)
		{
			if (string.IsNullOrEmpty(avatarId) || !Exists) return false;
			IntPtr db = IntPtr.Zero;
			try
			{
				byte[] pathBytes = Encoding.UTF8.GetBytes(DbPath + "\0");
				int rc = sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_URI, IntPtr.Zero);
				if (rc != SQLITE_OK || db == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] AddFavorite sqlite3_open_v2 failed: rc={rc}");
					return false;
				}
				sqlite3_busy_timeout(db, 3000);

				string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
				string safeId = SqlEscape(avatarId);
				string safeName = SqlEscape(name);
				string safeAuthor = SqlEscape(author);
				string safeImg = SqlEscape(imageUrl);
				string safeThumb = SqlEscape(!string.IsNullOrEmpty(thumbUrl) ? thumbUrl : imageUrl);

				// 1. Insert or update cache_avatar so VRCX client shows metadata and thumbnail
				string cacheSql = $@"
					INSERT OR REPLACE INTO cache_avatar (id, added_at, author_name, created_at, image_url, name, thumbnail_image_url, updated_at)
					VALUES ('{safeId}', '{now}', '{safeAuthor}', '{now}', '{safeImg}', '{safeName}', '{safeThumb}', '{now}');";
				int rc1 = ExecuteNonQuery(db, cacheSql);

				// 2. Insert into favorite_avatar
				string delSql = $"DELETE FROM favorite_avatar WHERE avatar_id = '{safeId}';";
				ExecuteNonQuery(db, delSql);

				string insSql = $@"
					INSERT INTO favorite_avatar (created_at, avatar_id, group_name)
					VALUES ('{now}', '{safeId}', 'Favorites');";
				int rc2 = ExecuteNonQuery(db, insSql);

				_lastStamp = CalcStamp();
				VRChatArchiveModPlugin.Logger.LogInfo($"[VrcxDb] AddFavorite saved {avatarId} ({name}) to VRCX.sqlite3 (cache_rc={rc1}, fav_rc={rc2})");
				return rc2 == SQLITE_OK;
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] AddFavorite threw: {ex.Message}");
				return false;
			}
			finally
			{
				if (db != IntPtr.Zero) { try { sqlite3_close(db); } catch { } }
			}
		}

		/// <summary>
		/// Removes an avatar from VRCX local favorite_avatar table.
		/// </summary>
		public static bool RemoveFavorite(string avatarId)
		{
			if (string.IsNullOrEmpty(avatarId) || !Exists) return false;
			IntPtr db = IntPtr.Zero;
			try
			{
				byte[] pathBytes = Encoding.UTF8.GetBytes(DbPath + "\0");
				int rc = sqlite3_open_v2(pathBytes, out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_URI, IntPtr.Zero);
				if (rc != SQLITE_OK || db == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] RemoveFavorite sqlite3_open_v2 failed: rc={rc}");
					return false;
				}
				sqlite3_busy_timeout(db, 3000);

				string safeId = SqlEscape(avatarId);
				string sql = $"DELETE FROM favorite_avatar WHERE avatar_id = '{safeId}';";
				int res = ExecuteNonQuery(db, sql);
				_lastStamp = CalcStamp();
				VRChatArchiveModPlugin.Logger.LogInfo($"[VrcxDb] RemoveFavorite removed {avatarId} from VRCX.sqlite3");
				return res == SQLITE_OK;
			}
			catch (Exception ex)
			{
				VRChatArchiveModPlugin.Logger.LogWarning($"[VrcxDb] RemoveFavorite threw: {ex.Message}");
				return false;
			}
			finally
			{
				if (db != IntPtr.Zero) { try { sqlite3_close(db); } catch { } }
			}
		}
	}
}
