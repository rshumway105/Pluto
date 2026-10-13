---
title: "<element>"
type: bod-element
status: draft
created: <yyyy-mm-dd>
group: "<SAP2000 group>"
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

### Checks

What `check_elements` reports for this element: the governing result over the group (and its ratio to the limit,
when one is given). Kind: joint, reaction, frame, area, area_stress, link, or design (SAP2000 steel design ratio).
Cases: blank for all, or names / wildcards separated by commas. Limit in the model's units. Needs the `group`
property (the SAP2000 group holding the element's objects).

| Quantity | Kind | Cases | Limit | Basis |
|---|---|---|---|---|
|  |  |  |  |  |

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
