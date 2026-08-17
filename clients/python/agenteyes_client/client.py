"""AgentEyes loopback Control API client (zero-dependency, standard library only).

This wraps the localhost REST Control API served by the AgentEyes app on
http://127.0.0.1:7882 (issue #75, epic #72). One method per route, names mirroring
the route. Every non-2xx response is parsed as the S1 error envelope
{ "error": <message>, "code": <short-code> } and raised as AgentEyesApiError so callers
never see a raw transport exception for an HTTP error response.

Transport is urllib from the Python standard library (no third-party dependency).
Return shapes mirror the JSON the API returns verbatim (dict / list), not a typed
model layer (see issue #75 assumption A6).

ASCII only. Each request and response is logged at DEBUG level.
"""

import json
import logging
import urllib.error
import urllib.parse
import urllib.request

__all__ = ["AgentEyesClient", "AgentEyesApiError", "DEFAULT_BASE_URL"]

DEFAULT_BASE_URL = "http://127.0.0.1:7882"

_log = logging.getLogger("agenteyes_client")


class AgentEyesApiError(Exception):
    """Raised for any non-2xx response from the Control API.

    Carries the parsed S1 error envelope:
      status  - the HTTP status code (int), e.g. 404
      code    - the short code string from the envelope, e.g. "not_found"
      message - the human-readable message from the envelope's "error" field
    """

    def __init__(self, status, code, message):
        self.status = status
        self.code = code
        self.message = message
        super().__init__("HTTP {0} {1}: {2}".format(status, code, message))


class AgentEyesClient:
    """Client for the AgentEyes loopback Control API.

    Example:
        client = AgentEyesClient()                       # defaults to http://127.0.0.1:7882
        print(client.version())                    # "0.8.2"
        page = client.recordings(limit=2)          # {"total": N, "items": [...]}
    """

    def __init__(self, base_url=DEFAULT_BASE_URL, timeout=30):
        """Create a client.

        base_url - the API root, default http://127.0.0.1:7882 (configurable per AC / scope).
        timeout  - per-request timeout in seconds.
        """
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        _log.debug("AgentEyesClient init: base_url=%s timeout=%s", self.base_url, self.timeout)

    # ---- system / discovery -------------------------------------------------

    def health(self):
        """GET /health -> { ok, app }."""
        return self._get("/health")

    def version(self):
        """GET /version -> the version string (e.g. "0.8.2"), not the envelope."""
        return self._get("/version")["version"]

    def status(self):
        """GET /status -> the live recording status object.

        The object carries State ("idle" or "recording"), Mode, Source,
        ElapsedSeconds, Level, and Dir, mirroring the server verbatim (A6).
        """
        return self._get("/status")

    def is_recording(self):
        """True when the app is currently recording.

        Convenience read derived from GET /status: returns
        status()["State"] == "recording". This is the readable form the skill
        and the AC5 round-trip use to assert recording=true/false; it is not a
        separate route, just a typed view of the one /status call.
        """
        return self.status().get("State") == "recording"

    def devices(self):
        """GET /devices -> { monitors, mics, dshow }."""
        return self._get("/devices")

    def discover(self):
        """GET / -> the discovery document { app, endpoints }."""
        return self._get("/")

    # ---- recordings (read) --------------------------------------------------

    def recordings(self, limit=50, offset=0):
        """GET /recordings?limit&offset -> { total, items[] }, newest-first."""
        query = urllib.parse.urlencode({"limit": limit, "offset": offset})
        return self._get("/recordings?" + query)

    def recording(self, recording_id):
        """GET /recordings/{id} -> the detail object.

        Raises AgentEyesApiError (status 404, code "not_found") for an unknown id.
        """
        return self._get("/recordings/" + urllib.parse.quote(recording_id, safe=""))

    def shots(self, recording_id):
        """GET /recordings/{id}/shots -> the marker/frame shot list."""
        return self._get("/recordings/" + urllib.parse.quote(recording_id, safe="") + "/shots")

    def transcript(self, recording_id):
        """GET /recordings/{id}/transcript -> { text, segments[] }.

        Raises AgentEyesApiError (404 not_found) when the recording has no transcript.
        """
        return self._get("/recordings/" + urllib.parse.quote(recording_id, safe="") + "/transcript")

    # ---- captures -----------------------------------------------------------

    def captures(self):
        """GET /captures -> the capture gallery list, newest-first."""
        return self._get("/captures")

    def capture_info(self):
        """GET /capture-info -> { defaultFolder, configuredOverride, saveFolder }."""
        return self._get("/capture-info")

    # ---- presets ------------------------------------------------------------

    def presets(self):
        """GET /presets -> the saved capture-preset list."""
        return self._get("/presets")

    # ---- live actions -------------------------------------------------------

    def screenshot(self, screen=1, region=None):
        """POST /screenshot {screen, region?} -> { file }."""
        body = {"screen": screen}
        if region is not None:
            body["region"] = region
        return self._post("/screenshot", body)

    def capture(self, mode="full", screen=None, region=None):
        """POST /capture {mode:full|monitor|region, screen?, region?} -> { file, width, height }."""
        body = {"mode": mode}
        if screen is not None:
            body["screen"] = screen
        if region is not None:
            body["region"] = region
        return self._post("/capture", body)

    def record_start(self, **opts):
        """POST /record/start -> the live status object.

        Accepts any of the documented start options as keyword arguments:
        preset, mode, screen, source, mic, region, denoise, gate, level,
        micVol, sysVol, fps. Raises AgentEyesApiError (409 conflict) if already recording.
        """
        return self._post("/record/start", dict(opts))

    def record_shot(self):
        """POST /record/shot -> { file } (a marker screenshot during recording)."""
        return self._post("/record/shot", {})

    def record_stop(self):
        """POST /record/stop -> the stop result (includes the produced File).

        Raises AgentEyesApiError (409 conflict) if not currently recording.
        """
        return self._post("/record/stop", {})

    # ---- transport ----------------------------------------------------------

    def _get(self, path):
        return self._request("GET", path, None)

    def _post(self, path, body):
        return self._request("POST", path, body)

    def _request(self, method, path, body):
        """Issue one HTTP request and return the parsed JSON body.

        On any non-2xx response, parse the { error, code } envelope and raise
        AgentEyesApiError. urllib raises HTTPError for >=400, which carries the response
        body, so the envelope is read from there - never leaked as a raw exception.
        """
        url = self.base_url + path
        data = None
        headers = {"Accept": "application/json"}
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        _log.debug("REQUEST %s %s body=%s", method, url, body)

        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read().decode("utf-8")
                _log.debug("RESPONSE %s %s -> %s %s", method, url, resp.status, raw)
                return self._parse_json(raw)
        except urllib.error.HTTPError as http_err:
            raw = ""
            try:
                raw = http_err.read().decode("utf-8")
            except Exception:
                raw = ""
            _log.debug("RESPONSE %s %s -> %s %s", method, url, http_err.code, raw)
            raise self._to_api_error(http_err.code, raw) from None

    @staticmethod
    def _parse_json(raw):
        if not raw:
            return None
        return json.loads(raw)

    @staticmethod
    def _to_api_error(status, raw):
        """Build an AgentEyesApiError from an error response body.

        The S1 server contract is a uniform envelope: every error response body is
        { "error": <message>, "code": <short-code> } (RestServer.Error). This reads
        that envelope. A body that is empty or not the envelope only happens for a
        transport-level failure the API did not author (e.g. a proxy 5xx); in that
        case the status-derived code keeps .code/.message usable rather than leaking
        a raw transport exception - the .status is always the truthful HTTP code.
        """
        if raw:
            try:
                parsed = json.loads(raw)
            except ValueError:
                parsed = None
            if isinstance(parsed, dict) and "error" in parsed and "code" in parsed:
                return AgentEyesApiError(status, parsed["code"], parsed["error"])
            if isinstance(parsed, dict):
                return AgentEyesApiError(status, parsed.get("code", _code_for_status(status)),
                                   parsed.get("error", raw))
        return AgentEyesApiError(status, _code_for_status(status), raw or "(no response body)")


def _code_for_status(status):
    """Mirror the server's status->code mapping for non-envelope error bodies."""
    return {
        400: "bad_request",
        404: "not_found",
        409: "conflict",
        503: "unavailable",
    }.get(status, "internal")
