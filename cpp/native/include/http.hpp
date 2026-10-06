#pragma once
#include <string>

// A tiny WinHTTP client. The mod is the HTTP CLIENT in this design, never a server: the game
// process must not open a listening port, so it polls the desktop client instead. Both calls BLOCK,
// so neither may run on the game thread -- the bridge has its own worker.
namespace VRCA::Http {

    struct Result {
        bool        ok = false;
        int         status = 0;
        std::string body;
        std::string error;
    };

    Result Get(std::string const& url, int timeoutMs = 15000);
    Result PostJson(std::string const& url, std::string const& json, int timeoutMs = 15000);

    // A GET with extra request headers and a chosen User-Agent, for VRChat's own API.
    //
    // VRChat REFUSES an empty or malformed User-Agent with a 403 telling you to identify yourself,
    // so the caller names the app and a CONTACT URL -- never a personal email, which would be
    // handed to a third party on every request.
    //
    // `headers` is raw "Name: value\r\n" lines, as WinHTTP wants them.
    Result GetWith(std::string const& url, std::string const& headers,
                   std::string const& userAgent, int timeoutMs = 15000);
}
