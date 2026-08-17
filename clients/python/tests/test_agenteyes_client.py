"""Unit tests for agenteyes_client (mocked HTTP - no running app required).

Every public client method is exercised on its success path; the error paths
additionally assert AgentEyesApiError with the right .status and .code (issue #75 AC7).

The HTTP layer is mocked by patching urllib.request.urlopen (success) and by
raising urllib.error.HTTPError (error envelope), so these tests are pure and
deterministic. ASCII only.

Run from the repo root:
    python -m pytest clients/python/tests
or:
    python -m unittest discover -s clients/python/tests
"""

import io
import json
import os
import sys
import unittest
import urllib.error
from unittest import mock

# Make the agenteyes_client package importable without an install step (A1).
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from agenteyes_client import DEFAULT_BASE_URL, AgentEyesApiError, AgentEyesClient  # noqa: E402


class _FakeResponse:
    """Minimal stand-in for the urlopen context-manager response."""

    def __init__(self, payload, status=200):
        self._raw = json.dumps(payload).encode("utf-8") if payload is not None else b""
        self.status = status

    def read(self):
        return self._raw

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def _http_error(status, code, message):
    """Build a urllib HTTPError whose body is the S1 error envelope."""
    body = json.dumps({"error": message, "code": code}).encode("utf-8")
    return urllib.error.HTTPError(
        url="http://127.0.0.1:7882/x", code=status, msg=message,
        hdrs=None, fp=io.BytesIO(body))


class SuccessPathTests(unittest.TestCase):
    """One success-path assertion per public method - the captured request line
    proves the method hits the right route/verb, and the parsed body is returned
    verbatim (A6)."""

    def setUp(self):
        self.client = AgentEyesClient()
        self.captured = {}

    def _patch(self, payload):
        """Patch urlopen to record the outgoing request and return payload."""
        def fake_urlopen(req, timeout=None):
            self.captured["method"] = req.get_method()
            self.captured["url"] = req.full_url
            self.captured["body"] = req.data.decode("utf-8") if req.data else None
            return _FakeResponse(payload)
        return mock.patch("urllib.request.urlopen", side_effect=fake_urlopen)

    def test_default_base_url(self):
        self.assertEqual(self.client.base_url, DEFAULT_BASE_URL)

    def test_configurable_base_url_strips_trailing_slash(self):
        c = AgentEyesClient(base_url="http://example.test:9999/")
        self.assertEqual(c.base_url, "http://example.test:9999")

    def test_health(self):
        with self._patch({"ok": True, "app": "AgentEyes"}):
            self.assertEqual(self.client.health(), {"ok": True, "app": "AgentEyes"})
        self.assertEqual(self.captured["method"], "GET")
        self.assertTrue(self.captured["url"].endswith("/health"))

    def test_version_returns_string_not_envelope(self):
        with self._patch({"app": "AgentEyes", "version": "0.8.2"}):
            self.assertEqual(self.client.version(), "0.8.2")
        self.assertTrue(self.captured["url"].endswith("/version"))

    def test_status(self):
        with self._patch({"State": "idle"}):
            self.assertEqual(self.client.status(), {"State": "idle"})
        self.assertTrue(self.captured["url"].endswith("/status"))

    def test_is_recording_true(self):
        with self._patch({"State": "recording"}):
            self.assertTrue(self.client.is_recording())

    def test_is_recording_false(self):
        with self._patch({"State": "idle"}):
            self.assertFalse(self.client.is_recording())

    def test_devices(self):
        with self._patch({"monitors": [{"Index": 1}], "mics": [], "dshow": []}):
            self.assertEqual(len(self.client.devices()["monitors"]), 1)
        self.assertTrue(self.captured["url"].endswith("/devices"))

    def test_discover(self):
        with self._patch({"app": "AgentEyes", "endpoints": ["GET /version"]}):
            self.assertIn("endpoints", self.client.discover())
        self.assertTrue(self.captured["url"].rstrip("/").endswith("7882"))

    def test_recordings_paging_query(self):
        with self._patch({"total": 0, "items": []}):
            self.client.recordings(limit=2, offset=3)
        self.assertIn("limit=2", self.captured["url"])
        self.assertIn("offset=3", self.captured["url"])

    def test_recording_detail(self):
        with self._patch({"id": "abc", "dir": "X"}):
            self.assertEqual(self.client.recording("abc")["id"], "abc")
        self.assertTrue(self.captured["url"].endswith("/recordings/abc"))

    def test_recording_id_is_url_quoted(self):
        with self._patch({"id": "a b"}):
            self.client.recording("a b/c")
        # Slash and space in the id must be percent-encoded so they cannot change the route.
        self.assertIn("/recordings/a%20b%2Fc", self.captured["url"])

    def test_shots(self):
        with self._patch([{"file": "s.png"}]):
            self.assertEqual(self.client.shots("abc")[0]["file"], "s.png")
        self.assertTrue(self.captured["url"].endswith("/recordings/abc/shots"))

    def test_transcript(self):
        with self._patch({"text": "hi", "segments": []}):
            self.assertEqual(self.client.transcript("abc")["text"], "hi")
        self.assertTrue(self.captured["url"].endswith("/recordings/abc/transcript"))

    def test_captures(self):
        with self._patch([{"file": "c.png"}]):
            self.assertEqual(len(self.client.captures()), 1)
        self.assertTrue(self.captured["url"].endswith("/captures"))

    def test_capture_info(self):
        with self._patch({"defaultFolder": "D", "configuredOverride": None, "saveFolder": "D"}):
            self.assertEqual(self.client.capture_info()["saveFolder"], "D")
        self.assertTrue(self.captured["url"].endswith("/capture-info"))

    def test_presets(self):
        with self._patch([{"Id": "p1", "Name": "Default"}]):
            self.assertEqual(self.client.presets()[0]["Name"], "Default")
        self.assertTrue(self.captured["url"].endswith("/presets"))

    def test_screenshot_default_screen(self):
        with self._patch({"file": "shot.png"}):
            self.assertEqual(self.client.screenshot()["file"], "shot.png")
        self.assertEqual(self.captured["method"], "POST")
        self.assertEqual(json.loads(self.captured["body"]), {"screen": 1})

    def test_screenshot_region(self):
        with self._patch({"file": "shot.png"}):
            self.client.screenshot(screen=2, region=[0, 0, 100, 50])
        self.assertEqual(json.loads(self.captured["body"]), {"screen": 2, "region": [0, 0, 100, 50]})

    def test_capture_mode(self):
        with self._patch({"file": "c.png", "width": 10, "height": 5}):
            self.client.capture(mode="full", screen=1)
        self.assertEqual(json.loads(self.captured["body"]), {"mode": "full", "screen": 1})

    def test_record_start_passes_opts(self):
        with self._patch({"State": "recording"}):
            self.client.record_start(mode="audio", screen=1, source="system")
        self.assertEqual(self.captured["method"], "POST")
        self.assertTrue(self.captured["url"].endswith("/record/start"))
        self.assertEqual(json.loads(self.captured["body"]),
                         {"mode": "audio", "screen": 1, "source": "system"})

    def test_record_shot(self):
        with self._patch({"file": "m.png"}):
            self.assertEqual(self.client.record_shot()["file"], "m.png")
        self.assertTrue(self.captured["url"].endswith("/record/shot"))

    def test_record_stop(self):
        with self._patch({"File": "out.mp4"}):
            self.assertEqual(self.client.record_stop()["File"], "out.mp4")
        self.assertTrue(self.captured["url"].endswith("/record/stop"))


class ErrorPathTests(unittest.TestCase):
    """Every non-2xx response is raised as AgentEyesApiError carrying .status/.code/.message;
    a raw transport HTTPError is never leaked to the caller (AC3)."""

    def setUp(self):
        self.client = AgentEyesClient()

    def _expect_error(self, call, status, code):
        err = _http_error(status, code, "boom")
        with mock.patch("urllib.request.urlopen", side_effect=err):
            with self.assertRaises(AgentEyesApiError) as ctx:
                call()
        self.assertEqual(ctx.exception.status, status)
        self.assertEqual(ctx.exception.code, code)
        self.assertEqual(ctx.exception.message, "boom")
        return ctx.exception

    def test_recording_unknown_id_404_not_found(self):
        # AC3: recording("<unknown>") -> AgentEyesApiError(.status==404, .code=="not_found").
        self._expect_error(lambda: self.client.recording("nope"), 404, "not_found")

    def test_transcript_missing_404(self):
        self._expect_error(lambda: self.client.transcript("nope"), 404, "not_found")

    def test_record_start_conflict_409(self):
        self._expect_error(lambda: self.client.record_start(mode="audio"), 409, "conflict")

    def test_record_stop_conflict_409(self):
        self._expect_error(self.client.record_stop, 409, "conflict")

    def test_capture_bad_request_400(self):
        self._expect_error(lambda: self.client.capture(mode="bogus"), 400, "bad_request")

    def test_error_is_not_raw_httperror(self):
        # The caller must see AgentEyesApiError, never urllib.error.HTTPError.
        err = _http_error(404, "not_found", "boom")
        with mock.patch("urllib.request.urlopen", side_effect=err):
            try:
                self.client.recording("nope")
                self.fail("expected AgentEyesApiError")
            except urllib.error.HTTPError:
                self.fail("raw HTTPError leaked to caller")
            except AgentEyesApiError as e:
                self.assertEqual(e.code, "not_found")

    def test_empty_error_body_still_typed(self):
        # A transport 5xx with no envelope body still yields a typed AgentEyesApiError.
        err = urllib.error.HTTPError(
            url="http://127.0.0.1:7882/x", code=500, msg="srv",
            hdrs=None, fp=io.BytesIO(b""))
        with mock.patch("urllib.request.urlopen", side_effect=err):
            with self.assertRaises(AgentEyesApiError) as ctx:
                self.client.status()
        self.assertEqual(ctx.exception.status, 500)
        self.assertEqual(ctx.exception.code, "internal")


if __name__ == "__main__":
    unittest.main()
