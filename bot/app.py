"""Minimal OAuth Card POC bot.

Architecture: aiohttp + botbuilder-python. One dialog with a single
OAuthPrompt step. When the prompt resolves, we dump the issuing IdP's claims
back to the user so we can confirm WHO they signed in as (proves the OAuth
Card flow actually completed).

Design intent — what we are testing:
- Does the OAuth Card surface in Teams chat?  (Expected: YES with Generic OAuth 2)
- Does the silent-completion postback finish the flow?  (Hopeful)
- Or do we get the magic code?  (Bad case — what we want to verify in Copilot)
"""

from __future__ import annotations

import json
import logging
import os
import sys
from typing import Any

from aiohttp import web
from botbuilder.core import (
    ActivityHandler,
    ConversationState,
    MemoryStorage,
    TurnContext,
    UserState,
)
from botbuilder.core.integration import aiohttp_error_middleware
from botbuilder.dialogs import (
    DialogSet,
    DialogTurnStatus,
    WaterfallDialog,
    WaterfallStepContext,
)
from botbuilder.dialogs.prompts import (
    OAuthPrompt,
    OAuthPromptSettings,
    PromptOptions,
)
from botbuilder.integration.aiohttp import (
    CloudAdapter,
    ConfigurationBotFrameworkAuthentication,
)
from botbuilder.schema import Activity, ActivityTypes
from dotenv import load_dotenv

load_dotenv(os.path.join(os.path.dirname(__file__), "..", ".env"))

logging.basicConfig(
    level=logging.DEBUG if os.getenv("BOT_DEBUG") else logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    stream=sys.stdout,
)
log = logging.getLogger("oauth_poc_bot")

APP_ID = os.environ["BOT_APP_ID"]
APP_PASSWORD = os.environ["BOT_APP_SECRET"]
APP_TENANTID = os.environ["TENANT_ID"]
CONNECTION_NAME = os.environ["OAUTH_CONNECTION_NAME"]
PORT = int(os.getenv("BOT_PORT", "8000"))


class BotConfig:
    """Mimics MicrosoftAppCredentialsConfiguration for SingleTenant bot."""

    APP_ID = APP_ID
    APP_PASSWORD = APP_PASSWORD
    APP_TYPE = "SingleTenant"
    APP_TENANTID = APP_TENANTID

    def __getattr__(self, name: str) -> Any:
        # Be permissive — botbuilder probes for many optional attrs
        return None


class OAuthPocDialog(WaterfallDialog):
    """One-shot dialog: prompt for OAuth, then echo identity claims."""

    def __init__(self, connection_name: str):
        super().__init__("OAuthPocDialog", [self.prompt_step, self.show_token_step])
        self._connection_name = connection_name

    async def prompt_step(self, step: WaterfallStepContext):
        log.info("prompt_step: invoking OAuthPrompt(connection=%s)", self._connection_name)
        return await step.begin_dialog("OAuthPrompt")

    async def show_token_step(self, step: WaterfallStepContext):
        token_response = step.result
        if not token_response or not getattr(token_response, "token", None):
            log.warning("show_token_step: no token, login likely cancelled")
            await step.context.send_activity("Login was cancelled or failed. Type anything to try again.")
            return await step.end_dialog()

        token = token_response.token
        log.info("show_token_step: got token, length=%d", len(token))

        # Decode the JWT payload (no signature verification — POC only).
        claims_summary = _decode_jwt_claims(token)
        await step.context.send_activity(
            f"✅ Sign-in completed. Claims from token:\n```\n{claims_summary}\n```"
        )
        return await step.end_dialog()


def _decode_jwt_claims(token: str) -> str:
    """Extract a few readable claims from a JWT. POC: no validation."""
    import base64

    try:
        _h, payload_b64, _s = token.split(".")
        # Pad base64 if needed
        padding = "=" * (-len(payload_b64) % 4)
        payload = json.loads(base64.urlsafe_b64decode(payload_b64 + padding))
        keep = ("aud", "iss", "name", "preferred_username", "upn", "tid", "oid", "scp", "appid")
        return json.dumps({k: payload.get(k) for k in keep if payload.get(k) is not None}, indent=2)
    except Exception as exc:  # pragma: no cover
        log.exception("failed to decode JWT")
        return f"(could not decode JWT payload: {exc})"


class OAuthPocBot(ActivityHandler):
    """Routes user activities into a DialogSet that runs OAuthPocDialog."""

    def __init__(self, conversation_state: ConversationState, user_state: UserState):
        super().__init__()
        self._conversation_state = conversation_state
        self._user_state = user_state
        self._dialog_state = conversation_state.create_property("DialogState")

        self._dialogs = DialogSet(self._dialog_state)
        self._dialogs.add(
            OAuthPrompt(
                "OAuthPrompt",
                OAuthPromptSettings(
                    connection_name=CONNECTION_NAME,
                    text="Please sign in",
                    title="Sign In",
                    timeout=300_000,
                ),
            )
        )
        self._dialogs.add(OAuthPocDialog(CONNECTION_NAME))

    async def on_turn(self, turn_context: TurnContext):
        log.info(
            "on_turn: type=%s text=%r name=%s",
            turn_context.activity.type,
            getattr(turn_context.activity, "text", None),
            getattr(turn_context.activity, "name", None),
        )
        await super().on_turn(turn_context)
        # Persist any state changes after each turn.
        await self._conversation_state.save_changes(turn_context, False)
        await self._user_state.save_changes(turn_context, False)

    async def on_members_added_activity(self, members_added, turn_context: TurnContext):
        for member in members_added:
            if member.id != turn_context.activity.recipient.id:
                await turn_context.send_activity(
                    "Welcome to the OAuth Card POC bot. Send any message and I'll trigger the sign-in flow."
                )

    async def on_message_activity(self, turn_context: TurnContext):
        text = (turn_context.activity.text or "").strip().lower()
        if text == "/logout":
            await self._sign_out(turn_context)
            return
        if text == "/reset":
            await self._reset_dialogs(turn_context)
            await turn_context.send_activity("Dialog state reset. Send any message to start over.")
            return
        await self._run_dialog(turn_context)

    async def on_token_response_event(self, turn_context: TurnContext):
        # Fired when OAuthPrompt receives a token via TokenResponse event
        log.info("on_token_response_event: relaying to dialog")
        await self._run_dialog(turn_context)

    async def on_invoke_activity(self, turn_context: TurnContext):
        # signin/verifyState (magic code path) and signin/tokenExchange (silent path)
        log.info(
            "on_invoke_activity: name=%s value=%r",
            turn_context.activity.name,
            turn_context.activity.value,
        )
        if turn_context.activity.name in ("signin/verifyState", "signin/tokenExchange"):
            await self._run_dialog(turn_context)
            from botbuilder.schema import InvokeResponse
            return InvokeResponse(status=200)
        return await super().on_invoke_activity(turn_context)

    async def _run_dialog(self, turn_context: TurnContext):
        dialog_context = await self._dialogs.create_context(turn_context)
        result = await dialog_context.continue_dialog()
        if result.status == DialogTurnStatus.Empty:
            await dialog_context.begin_dialog("OAuthPocDialog")

    async def _sign_out(self, turn_context: TurnContext):
        from botframework.connector.auth import UserTokenClient
        # Always cancel any in-flight dialog so the next user message gets
        # a fresh OAuth Card (otherwise the stuck OAuthPrompt silently swallows
        # subsequent messages thinking they are magic-code responses).
        await self._reset_dialogs(turn_context)
        token_client: UserTokenClient = turn_context.turn_state.get(
            "UserTokenClient"
        )
        if token_client:
            await token_client.sign_out_user(
                turn_context.activity.from_property.id,
                CONNECTION_NAME,
                turn_context.activity.channel_id,
            )
        await turn_context.send_activity("Signed out. Send any message to sign in again.")

    async def _reset_dialogs(self, turn_context: TurnContext):
        dialog_context = await self._dialogs.create_context(turn_context)
        await dialog_context.cancel_all_dialogs()
        log.info("_reset_dialogs: cancelled all dialogs")


# ---- aiohttp wiring ----

storage = MemoryStorage()
conversation_state = ConversationState(storage)
user_state = UserState(storage)
bot = OAuthPocBot(conversation_state, user_state)

adapter = CloudAdapter(ConfigurationBotFrameworkAuthentication(BotConfig()))


async def on_error(context: TurnContext, error: Exception):
    log.exception("Adapter error: %s", error)
    await context.send_activity(f"Bot error: {error}")


adapter.on_turn_error = on_error


async def messages(req: web.Request) -> web.Response:
    body = await req.json() if req.content_length else {}
    activity = Activity().deserialize(body)
    auth_header = req.headers.get("Authorization", "")
    response = await adapter.process_activity(auth_header, activity, bot.on_turn)
    if response:
        return web.json_response(data=response.body, status=response.status)
    return web.Response(status=201)


async def health(_req: web.Request) -> web.Response:
    return web.json_response({
        "status": "ok",
        "app_id": APP_ID,
        "connection_name": CONNECTION_NAME,
        "tenant_id": APP_TENANTID,
    })


def main():
    app = web.Application(middlewares=[aiohttp_error_middleware])
    app.router.add_post("/api/messages", messages)
    app.router.add_get("/health", health)
    log.info(
        "Starting on :%d  app_id=%s  connection=%s  tenant=%s",
        PORT, APP_ID, CONNECTION_NAME, APP_TENANTID,
    )
    web.run_app(app, host="0.0.0.0", port=PORT)


if __name__ == "__main__":
    main()
