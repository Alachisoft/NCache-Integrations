# NCache Entity Framework Core

## Overview

### Query Caching

- Query caching involves storing transactional query results in cache using the EF Core extension APIs (`FromCache`/`FromCacheAsync`).
- Upon execution of a query, the cache is checked for the query result.
    - If found, the database is queried and the result is cached.
    - Otherwise the result is fetched from the cache instead of the database. 

## References

Reference documentation is available at:\
https://www.alachisoft.com/resources/docs/ncache/prog-guide/entity-framework-core-caching.html

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