# NCache SIGNALR

## Overview

SignalR is an ASP.NET Core library for real-time communication between server and clients.
It automatically manages persistent connections using WebSockets, Server-Sent Events, or Long Polling depending on capability.
It enables server-side code to push messages instantly to connected clients.
It abstracts connection management, reconnection, and transport negotiation.
Common use cases include chat systems, live dashboards, notifications, and real-time updates.

In a multi-server SignalR setup, a backplane is used to synchronize messages across all server instances.
The NCache SignalR backplane uses a distributed cache to propagate messages between nodes in a cluster.
When one server sends a SignalR message, NCache ensures all other servers receive and forward it to their connected clients.
This allows horizontal scaling of SignalR applications without losing message consistency.
It improves performance and reliability by avoiding a single central broker and using a high-speed distributed cache instead.

## References

Reference documentation is available at:\
https://www.alachisoft.com/resources/docs/ncache/prog-guide/ncache-extension-signalr.html?tabs=net

## Additional Resources

### Samples & Playground

For more samples of NCache features on various platforms:\
https://github.com/Alachisoft/NCache-Samples/

You can also visit NCache Playground for an interactive feature demo:\
https://www.alachisoft.com/nclive/

### Documentation

The complete online documentation for NCache is available at:\
http://www.alachisoft.com/resources/docs/#ncache

### Programmer's Guide
The complete programmer's guide of NCache is available at:\
http://www.alachisoft.com/resources/docs/ncache/prog-guide/

## Technical Support

Alachisoft&copy; provides various sources of technical support. 

- Please refer to http://www.alachisoft.com/support.html to select a support resource you find suitable for your issue.
- To request additional features in the future, or if you notice any discrepancy regarding this document, please drop an email to [support@alachisoft.com](mailto:support@alachisoft.com).

## Copyrights

Copyright 2026 Alachisoft&copy;