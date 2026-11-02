---
title: NorthStar Card Network Dispute Rules Summary
doc_type: network_rule
dispute_types: [duplicate_charge, incorrect_amount, credit_not_processed, cancelled_recurring, not_received, unauthorised]
version: "2026.2"
effective_from: 2026-04-01
effective_to:
owner: compliance
approved_by: head-of-disputes
audience: [analyst, senior_analyst]
language: en
---

# NorthStar Card Network Dispute Rules Summary

NorthStar is the (fictional) card network that carries Maple Bank's cards. This is Maple Bank's internal summary of the network's dispute rules, for analysts. Where it differs from the Card Dispute Policy, the policy decides what the customer is told and credited; this summary decides how the bank recovers the money from the merchant's bank.

## 1 How a dispute moves through the network

1. Maple Bank (the **issuer**) gives the customer a provisional credit under the Card Dispute Policy.
2. The issuer files a **chargeback** with NorthStar under a reason code (section 2), with the evidence.
3. The merchant's bank (the **acquirer**) has **30 days** to accept or send evidence back (a **representment**).
4. If the merchant proves the charge was valid, the issuer may reverse the provisional credit, after telling the customer in writing.

## 2 Reason codes

| Reason code | Name                                 | Maple Bank dispute type | Notes                                               |
| ----------- | ------------------------------------ | ----------------------- | --------------------------------------------------- |
| NS-10.4     | Card-absent fraud                    | unauthorised            | Filed by the fraud team only                        |
| NS-12.1     | Duplicate processing                 | duplicate_charge        | Same card, amount, merchant and date                |
| NS-12.2     | Incorrect transaction amount         | incorrect_amount        | Needs the receipt or invoice with the agreed amount |
| NS-13.1     | Merchandise or services not received | not_received            | Only after the expected delivery date               |
| NS-13.6     | Credit not processed                 | credit_not_processed    | Needs proof of return or cancellation               |
| NS-13.7     | Cancelled recurring transaction      | cancelled_recurring     | Needs the cancellation date                         |

## 3 Filing windows

The network accepts a chargeback up to **120 days** after the transaction's processing date. The Card Dispute Policy's customer time limits are set so that the bank can always file within this window.

## 4 Evidence standards

- Receipts, invoices and confirmations must show the merchant, the date and the amount.
- An image the analyst can't read with confidence is not evidence; ask the customer for a better copy.
- For foreign-currency purchases, the network compares the **original currency amount**. A difference caused only by conversion and fees is not a valid reason for a chargeback.

## 5 Merchant contact

For reason codes NS-13.1 and NS-13.6 the cardholder must have tried to resolve the problem with the merchant first. Record when and how they contacted the merchant.

---
*Maple Bank is fictional, and so is the NorthStar network. This document is synthetic, written for a training project; real card network rulebooks are confidential and much longer.*
