using System;
using UnityEngine;
using VRC.Localization;

namespace VRChatArchiveMod.Core
{
	public static class VrcKeyboard
	{
		public static bool Open(string title, string initialText, Action<string> onComplete, string placeholder = "", bool isNumeric = false)
		{
			// Calling VRCUiPopupManager with managed delegates crashes VRChat (0xc000001d) due to IL2CPP delegate trampolines.
			// Direct inline typing and clipboard paste are used instead.
			return false;
		}
	}
}
