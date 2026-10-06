#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <winhttp.h>
#pragma comment(lib, "winhttp.lib")

#include "http.hpp"

#include <string>
#include <vector>

namespace VRCA::Http {

namespace {

    std::wstring Widen(const std::string& s) {
        if (s.empty()) return {};
        int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
        std::wstring w(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), w.data(), n);
        return w;
    }

    // One code path for both verbs: everything except the method, the body and the content type is
    // identical, and two near-copies of WinHTTP boilerplate is how one of them ends up subtly wrong.
    Result Send(const std::string& url, wchar_t const* verb,
                const std::string* body, int timeoutMs,
                const std::string* extraHeaders = nullptr,
                const std::string* userAgent = nullptr) {
        Result r;
        std::wstring wurl = Widen(url);

        URL_COMPONENTS uc{};
        uc.dwStructSize = sizeof(uc);
        wchar_t host[256]{}, path[2048]{};
        uc.lpszHostName = host; uc.dwHostNameLength = 255;
        uc.lpszUrlPath  = path; uc.dwUrlPathLength  = 2047;
        if (!WinHttpCrackUrl(wurl.c_str(), 0, 0, &uc)) { r.error = "url invalide"; return r; }

        // NO PROXY. The bridge lives on 127.0.0.1 and a configured system proxy would happily try
        // to route loopback through itself and fail; nothing here ever needs to leave the machine.
        // The caller's User-Agent when it gave one: VRChat's API answers 403 to an empty or
        // malformed one, so a request that must reach it identifies itself properly.
        std::wstring wua = userAgent ? Widen(*userAgent) : std::wstring(L"VRChatArchive/3.0");
        HINTERNET session = WinHttpOpen(wua.c_str(),
                                        WINHTTP_ACCESS_TYPE_NO_PROXY,
                                        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
        if (!session) { r.error = "WinHttpOpen"; return r; }
        WinHttpSetTimeouts(session, 5000, 5000, timeoutMs, timeoutMs);

        HINTERNET connect = WinHttpConnect(session, host, uc.nPort, 0);
        if (!connect) { WinHttpCloseHandle(session); r.error = "WinHttpConnect"; return r; }

        const DWORD flags = (uc.nScheme == INTERNET_SCHEME_HTTPS) ? WINHTTP_FLAG_SECURE : 0;
        HINTERNET req = WinHttpOpenRequest(connect, verb, path, nullptr,
                                           WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
        if (!req) {
            WinHttpCloseHandle(connect); WinHttpCloseHandle(session);
            r.error = "WinHttpOpenRequest"; return r;
        }

        std::wstring hdr;
        if (body) hdr += L"Content-Type: application/json\r\n";
        if (extraHeaders && !extraHeaders->empty()) hdr += Widen(*extraHeaders);
        wchar_t const* headers = hdr.empty() ? WINHTTP_NO_ADDITIONAL_HEADERS : hdr.c_str();
        DWORD headerLen = hdr.empty() ? 0 : DWORD(-1L);
        void*  data     = body ? const_cast<char*>(body->data()) : WINHTTP_NO_REQUEST_DATA;
        DWORD  dataLen  = body ? static_cast<DWORD>(body->size()) : 0;

        if (WinHttpSendRequest(req, headers, headerLen, data, dataLen, dataLen, 0) &&
            WinHttpReceiveResponse(req, nullptr)) {

            DWORD status = 0, len = sizeof(status);
            WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                                WINHTTP_HEADER_NAME_BY_INDEX, &status, &len, WINHTTP_NO_HEADER_INDEX);
            r.status = static_cast<int>(status);

            DWORD avail = 0;
            do {
                avail = 0;
                if (!WinHttpQueryDataAvailable(req, &avail) || avail == 0) break;
                std::string chunk(avail, '\0');
                DWORD read = 0;
                if (!WinHttpReadData(req, chunk.data(), avail, &read)) break;
                chunk.resize(read);
                r.body += chunk;
                if (r.body.size() > 16u * 1024u * 1024u) break;
            } while (avail > 0);

            r.ok = (r.status >= 200 && r.status < 300);
            if (!r.ok) r.error = "HTTP " + std::to_string(r.status);
        } else {
            r.error = "send refused (" + std::to_string(GetLastError()) + ")";
        }

        WinHttpCloseHandle(req);
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return r;
    }
}

Result GetWith(const std::string& url, const std::string& headers,
               const std::string& userAgent, int timeoutMs) {
    return Send(url, L"GET", nullptr, timeoutMs, &headers, &userAgent);
}

Result Get(const std::string& url, int timeoutMs) {
    return Send(url, L"GET", nullptr, timeoutMs);
}

Result PostJson(const std::string& url, const std::string& json, int timeoutMs) {
    return Send(url, L"POST", &json, timeoutMs);
}

}
