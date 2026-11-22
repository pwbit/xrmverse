# Philosophy

[← xrmverse documentation](../README.md#documentation)

## Introduction

This page explains the principles behind xrmverse: why it is built the way it is, and what you can expect from it when it
cannot behave exactly like Dataverse. Every other page of this manual applies these principles to one feature.

| Principle | In one line |
|---|---|
| [The contract, not an imitation](#the-contract-not-an-imitation) | Same endpoints, messages, tables, solution format and error codes |
| [One semantics](#one-semantics) | Every client goes through the same Core, pipeline and security |
| [Your data, your hardware](#your-data-your-hardware) | On-premises is the reason the project exists |
| [Self-sufficiency](#self-sufficiency) | A complete, cross-platform CLI |
| [Built for AI agents](#built-for-ai-agents) | A built-in MCP server |
| [Porting first, then innovation](#porting-first-then-innovation) | Run existing apps first, then go further |

## The contract, not an imitation

The goal is not to "look like" Dataverse but to **honour its contract**: the same endpoints, the same SDK messages, the same
system table names, the same solution format, the same error codes. Success means an existing client cannot tell the
difference.

Three rules follow:

| Rule | What it means for you |
|---|---|
| **Drop-in first** | Every extension of our own uses the `xrmverse_` prefix and never alters standard objects |
| **Fail visibly** | Anything that cannot be reproduced faithfully is rejected with an explicit error, never silently executed differently |
| **Solution round-trip** | A solution exported from xrmverse can still be imported into Dataverse |

Because of "fail visibly", each page of this manual ends with a **Known limits** chapter: what is refused, what differs and
what is not verified. The full list is in [Known issues and limitations](known-issues.md).

## One semantics

Everything goes through a single **Core** that implements `IOrganizationService`: the Web API, the CLI, MCP, solution import
and the plug-ins themselves. There are no "privileged paths" that skip the pipeline or security. If a rule applies to one
client, it applies to all of them.

## Your data, your hardware

On-premises is not a secondary mode, it is the reason the project exists:

- data under your control;
- no mandatory cloud dependency;
- a one-command install;
- direct database access for backup and monitoring.

## Self-sufficiency

XrmToolBox is Windows-only and `pac` does not cover everything (it cannot create tables or register plug-ins). That is why
the `xrmd` CLI is **complete and cross-platform**: you can manage an environment from Windows, macOS or Linux with no other
tool (see [CLI xrmd](cli.md)).

## Built for AI agents

xrmverse ships with a **built-in MCP server**: agents (Copilot Studio, Claude and others) query data and manage the schema with
the authenticated user's permissions, with no integration code (see [MCP](mcp.md)).

## Porting first, then innovation

For the UI the order is deliberate: first run existing model-driven apps as they are, then let them move past Power Apps'
limits with a completely different look (see [Model-driven apps](model-driven-apps.md)).
