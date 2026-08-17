"""agenteyes_client - a local Python client for the AgentEyes loopback Control API.

Import the client and the typed error:

    from agenteyes_client import AgentEyesClient, AgentEyesApiError

See client.py for the full method surface (issue #75, epic #72).
"""

from .client import DEFAULT_BASE_URL, AgentEyesApiError, AgentEyesClient

__all__ = ["AgentEyesClient", "AgentEyesApiError", "DEFAULT_BASE_URL"]
