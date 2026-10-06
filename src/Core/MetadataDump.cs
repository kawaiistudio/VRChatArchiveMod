using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VRChatArchiveMod.Core
{
	// THE WHOLE 1903 METADATA, COPIED OUT ONCE, FROM INSIDE THE PROCESS.
	//
	// WHY THIS EXISTS
	//
	// Everything known about this build was learned one in-game probe at a time: launch, look, read the
	// log, deduce, patch, relaunch. That costs the owner playing time, it burns a session per fact, and
	// three times a probe of mine took the game down with it. With the real metadata in hand none of it
	// is necessary: every class, field and method name of 1903 becomes an offline lookup.
	//
	// The file on disk is encrypted and this build does NOT use either key the pack notes recorded --
	// neither (0x51 - i) nor (0x58 - i) produces the sanity magic (checked 2026-09-18). But il2cpp
	// refuses to start unless the header it is handed reads sanity == 0xFAB11BAF, so the process is
	// holding the whole thing in PLAINTEXT regardless of what the file does. Decryption is not a problem
	// to solve here; it is a problem to walk around.
	//
	// WHY IN-PROCESS RATHER THAN THE POWERSHELL DUMPER
	//
	// The owner's VRChat runs elevated, so OpenProcess from an ordinary shell is refused -- the same
	// reason its executable path reads back empty. The mod is already inside that process and needs no
	// permission at all to read its own memory.
	//
	// SAFETY
	//
	// Nothing here touches il2cpp. It runs on a BACKGROUND thread, so a multi-second scan cannot stutter
	// a frame; it runs at most once, the output file being the "already done" marker; and every read goes
	// through ReadProcessMemory, which REFUSES rather than faults. That last point is the whole lesson of
	// this file -- see the note on the import.
	internal static unsafe class MetadataDump
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct MEMORY_BASIC_INFORMATION
		{
			public IntPtr BaseAddress, AllocationBase;
			public int AllocationProtect;
			public IntPtr RegionSize;
			public int State, Protect, Type;
		}

		[DllImport("kernel32")]
		private static extern IntPtr VirtualQuery(IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);

		// READ OUR OWN MEMORY THE WAY A DEBUGGER READS SOMEONE ELSE'S, AND FOR THE SAME REASON.
		//
		// The first version dereferenced pages directly. The breadcrumb recorded exactly what that cost:
		// two launches written, neither returned -- the scan killed the game twice. VirtualQuery saying a
		// region is committed is a statement about the past, not a promise. VRChat frees large buffers
		// constantly while a world loads, and a private block of tens of megabytes is precisely what an
		// asset bundle in flight looks like. Touch it a microsecond after it goes and the fault is ours,
		// and no try/catch takes it back.
		//
		// ReadProcessMemory on our OWN process returns FALSE for memory that is no longer there instead
		// of faulting. That is the entire difference, and it is why the PowerShell dumper was safe while
		// this was not. The copy costs a little time; it removes the failure mode completely.
		[DllImport("kernel32", SetLastError = true)]
		private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

		[DllImport("kernel32")]
		private static extern IntPtr GetCurrentProcess();

		private const int MEM_COMMIT = 0x1000;
		private const int PAGE_GUARD = 0x100;
		private const int PAGE_NOACCESS = 0x01;

		private static string OutPath
		{
			get
			{
				try { return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "VRChatArchiveMod", "global-metadata.1903.DECRYPTED.dat"); }
				catch { return "global-metadata.1903.DECRYPTED.dat"; }
			}
		}

		private static string FlagPath
		{
			get
			{
				try { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "va_metadump.flag"); }
				catch { return null; }
			}
		}

		internal static void StartOnce()
		{
			try
			{
				string outp = OutPath;
				if (System.IO.File.Exists(outp))
				{
					long len = 0; try { len = new System.IO.FileInfo(outp).Length; } catch { }
					if (len > 1024 * 1024) return;      // already have it
				}

				// THREE TRIES, NOT ONE.
				//
				// The first version banned the dump the moment it found a breadcrumb, and that fired on a
				// perfectly healthy run: the owner simply relaunched while the scan was still going. To a
				// one-shot marker "you quit mid-scan" and "the scan killed the game" look identical, so it
				// punished the ordinary case. Counting separates them -- a real crash loop still trips it,
				// an interrupted launch does not.
				string flag = FlagPath;
				int tries = 0;
				try { if (flag != null && System.IO.File.Exists(flag)) int.TryParse(System.IO.File.ReadAllText(flag).Trim(), out tries); }
				catch { }
				if (tries >= 3)
				{
					try { System.IO.File.Delete(flag); } catch { }
					VRChatArchiveModPlugin.Logger.LogWarning("[MetaDump] trois balayages de suite ne sont pas revenus — dump DESACTIVE.");
					return;
				}

				int attempt = tries + 1;
				var t = new Thread(() => Run(outp, flag, attempt)) { IsBackground = true, Name = "VA-MetaDump" };
				t.Start();
			}
			catch (Exception e) { VRChatArchiveModPlugin.Logger.LogWarning("[MetaDump] " + e.Message); }
		}

		private static void Run(string outp, string flag, int attempt)
		{
			try
			{
				try { if (flag != null) System.IO.File.WriteAllText(flag, attempt.ToString()); } catch { }

				// il2cpp loads its metadata long before any plugin runs, but give the process a moment to
				// settle so the scan is not competing with startup allocation.
				Thread.Sleep(3000);

				IntPtr addr = IntPtr.Zero;
				long scanned = 0;
				IntPtr foundAt = IntPtr.Zero;
				long blobLen = 0;
				int regions = 0, magicHits = 0;
				long firstOffSeen = -1;
				IntPtr self = GetCurrentProcess();

				while (foundAt == IntPtr.Zero)
				{
					MEMORY_BASIC_INFORMATION mbi;
					if (VirtualQuery(addr, out mbi, (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero) break;

					long size = (long)mbi.RegionSize;
					long baseAddr = (long)mbi.BaseAddress;
					if (size <= 0) break;

					int prot = mbi.Protect & 0xFF;
					bool readable = prot == 0x02 || prot == 0x04 || prot == 0x08 || prot == 0x20 || prot == 0x40 || prot == 0x80;
					bool guarded = (mbi.Protect & PAGE_GUARD) != 0 || prot == PAGE_NOACCESS;

					// EVERY COMMITTED READABLE REGION, IN CHUNKS.
					//
					// The first pass looked only at PRIVATE blocks of 8 MB or more and found nothing in
					// 2287 MB across 46 regions. That filter was a guess dressed up as an optimisation:
					// VirtualQuery splits one allocation wherever protection changes, so a 38 MB buffer can
					// be reported as several smaller regions, and a mapped block is not PRIVATE at all.
					// Narrowing by type and size is only safe once you already know the answer.
					//
					// With ReadProcessMemory there is no fault left to avoid, so the filter bought nothing
					// and cost the result. Chunks overlap by a header length so a match cannot be lost on
					// a seam.
					if (mbi.State == MEM_COMMIT && readable && !guarded && size >= 0x1000)
					{
						regions++;
						const long CHUNK = 16L * 1024 * 1024;
						for (long off = 0; off < size && foundAt == IntPtr.Zero; off += CHUNK - 0x150)
						{
							long take = size - off;
							if (take > CHUNK) take = CHUNK;
							if (take <= 0x150) break;

							var page = new byte[take];
							IntPtr got;
							if (!ReadProcessMemory(self, (IntPtr)(baseAddr + off), page, (IntPtr)take, out got))
								break;   // gone between the query and the read: next region, not a fault

							long have = (long)got;
							scanned += have;
							long lim = have - 0x150;
							fixed (byte* p = page)
							{
								for (long i = 0; i < lim; i++)
								{
									// magic 0xFAB11BAF then version 29
									if (p[i] != 0xAF || p[i + 1] != 0x1B || p[i + 2] != 0xB1 || p[i + 3] != 0xFA) continue;
									if (p[i + 4] != 0x1D || p[i + 5] != 0x00 || p[i + 6] != 0x00 || p[i + 7] != 0x00) continue;

									magicHits++;
									long end;
									if (!HeaderLooksReal(p + i, out end))
									{
										// Say WHY it was refused. "Never seen" and "seen and rejected" are
										// different problems, and exactly one log line apart.
										if (firstOffSeen < 0) firstOffSeen = *(uint*)(p + i + 8);
										continue;
									}
									foundAt = (IntPtr)(baseAddr + off + i);
									blobLen = end;
									break;
								}
							}
						}
					}

					long next = baseAddr + size;
					if (next <= baseAddr) break;
					addr = (IntPtr)next;
				}

				if (foundAt == IntPtr.Zero)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[MetaDump] header introuvable apres " + regions
						+ " region(s), " + (scanned / (1024 * 1024)) + " Mo balayes. Magies 0xFAB11BAF vues : "
						+ magicHits + (firstOffSeen >= 0 ? " (1er offset lu : " + firstOffSeen + ", attendu 336)" : ""));
					return;
				}

				if (blobLen <= 0 || blobLen > 400L * 1024 * 1024)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[MetaDump] longueur de bloc invalide : " + blobLen);
					return;
				}

				var buf = new byte[blobLen];
				IntPtr copied;
				if (!ReadProcessMemory(self, foundAt, buf, (IntPtr)blobLen, out copied) || (long)copied < blobLen)
				{
					VRChatArchiveModPlugin.Logger.LogWarning("[MetaDump] lecture du bloc incomplete : "
						+ (long)copied + " / " + blobLen + " octets.");
					return;
				}

				try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outp)); } catch { }
				System.IO.File.WriteAllBytes(outp, buf);

				uint magic = BitConverter.ToUInt32(buf, 0);
				VRChatArchiveModPlugin.Logger.LogInfo("[MetaDump] METADATA 1903 EXTRAITE : " + (blobLen / (1024 * 1024))
					+ " Mo -> " + outp + " | controle magic=0x" + magic.ToString("X8")
					+ " version=" + BitConverter.ToInt32(buf, 4) + " (balaye " + (scanned / (1024 * 1024))
					+ " Mo sur " + regions + " region(s)).");
			}
			catch (Exception e)
			{
				VRChatArchiveModPlugin.Logger.LogWarning("[MetaDump] " + e.GetType().Name + " : " + e.Message);
			}
			finally
			{
				try { if (flag != null) System.IO.File.Delete(flag); } catch { }
			}
		}

		// The v29 header is 0x150 bytes of (offset,size) pairs after magic+version. A real one starts at
		// offset 336 and its offsets never go backwards; random bytes that happen to carry the magic do
		// not satisfy both. `end` comes back as the furthest byte any section reaches -- the blob length.
		private static bool HeaderLooksReal(byte* h, out long end)
		{
			end = 0;
			try
			{
				long prev = -1, max = 0;
				int pairs = 0;
				for (int j = 8; j + 8 <= 0x150; j += 8)
				{
					uint off = *(uint*)(h + j);
					uint sz = *(uint*)(h + j + 4);
					if (pairs == 0 && off != 336) return false;
					if (off < prev) return false;
					if (off > 400u * 1024 * 1024 || sz > 400u * 1024 * 1024) return false;
					prev = off;
					long e = (long)off + sz;
					if (e > max) max = e;
					pairs++;
				}
				if (pairs < 20 || max < 1024 * 1024) return false;
				end = max;
				return true;
			}
			catch { return false; }
		}
	}
}
