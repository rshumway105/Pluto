---
title: "<element>"
type: bod-element
status: draft
created: <yyyy-mm-dd>
---

# <element>

> [!info] Basis-of-design element note (Pluto template). One per design element. Written by hand; Pluto never
> edits it. The design records that support it link here through their `elements` property and are listed at the end.

## Description

What the element is, where it is, and what it does.

## Design criteria

- Codes and editions:
- Limit states and acceptance criteria (stress, deflection, drift):
- Category / importance factors:

## Loads

| Load | Value | Basis |
|---|---|---|
|  |  |  |

Combinations used:

## Conditions and assumptions

What holds for this element whatever the model: supports, existing structure, site and supplied data, each with its
basis. Assumptions tied to one model are in that model's design records.

## Models

- 

## Governing checks

| Check | Governing case | Demand | Capacity | Ratio | Record |
|---|---|---|---|---|---|
|  |  |  |  |  |  |

## Design records

```base
filters:
  and:
    - 'type == "design-record"'
    - 'file.hasLink(this.file)'
views:
  - type: table
    name: Records for this element
    order:
      - file.name
      - model
      - date
      - author
      - status
```
