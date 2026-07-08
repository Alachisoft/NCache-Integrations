# NCache.Client.Extension

## Overview

This library provides methods for distributed locking/unlocking that is used in several of the NCache integrations. It acts as a replacement for  [NCache Pessimistic Locking](https://www.alachisoft.com/resources/docs/ncache/prog-guide/locking-cache-items.html?tabs=net%2Cnet1%2Cnet2%2Cnet3%2Cnet4%2Cnet5) which is not available in NCache Open Source. 


## Usage

Obtain a lock

```csharp
LockKey(ICache cache,
        string key,
        LockToken? lockToken,
        TimeSpan? expirationTime);
```
Release lock

```csharp
UnlockKey(ICache cache, string key, LockToken lockHandle);
```
