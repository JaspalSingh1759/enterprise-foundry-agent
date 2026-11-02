---
title: Card Dispute Policy
doc_type: policy
dispute_types: [duplicate_charge, incorrect_amount, credit_not_processed, cancelled_recurring, not_received, unauthorised]
version: "3.1"
effective_from: 2026-11-01
effective_to:
owner: compliance
approved_by: head-of-disputes
audience: [analyst, senior_analyst]
language: en
---

# Card Dispute Policy

Version 3.1, in force for transactions dated on or after November 1, 2026. Transactions dated before that are judged under version 3.0 (`policy/archive/card-dispute-policy-v3.0.md`).

## 1 Purpose

This policy sets out how Maple Bank handles a cardholder's dispute of a charge on a Maple Bank credit card: who is eligible, what evidence is needed, how much is credited, and who must approve it.

## 2 Scope

It applies to every Maple Bank credit card (Maple Cashback Visa and Maple Travel Visa) and every dispute raised by the cardholder through any channel. Fraud investigations are run by the fraud team under its own procedures; this policy only covers sending a case to them (section 4.1).

## 3 General rules

### 3.1 Time limits

A dispute must be raised within the time limit for its type. The limit is counted in days from the transaction's **posting date** to the date the customer **submitted** the dispute. The version of this policy that applies is the one in force on the **transaction date**.

| Dispute type                                  | Time limit |
| --------------------------------------------- | ---------- |
| Duplicate charge (`duplicate_charge`)         | 120 days   |
| Incorrect amount (`incorrect_amount`)         | 120 days   |
| Credit not processed (`credit_not_processed`) | 120 days   |
| Cancelled recurring (`cancelled_recurring`)   | 120 days   |
| Not received (`not_received`)                 | 120 days   |
| Unauthorised (`unauthorised`)                 | 120 days   |

A dispute raised after the limit is **not eligible**, whatever the evidence shows. The outcome is a denial that cites this section.

### 3.2 Provisional credit

When a dispute is eligible, the customer receives a **provisional credit** for the disputed amount while the bank recovers the money through the card network (see `network-rules/northstar-dispute-rules.md`). A provisional credit is never more than the disputed amount, and never includes interest or fees unless a later section says so.

### 3.3 Approval

Every outcome is approved by a person before it takes effect.

Senior approval threshold: $1,000.00 CAD.

- An **analyst** may approve a credit of up to and including $1,000.00.
- A credit of **more than $1,000.00** must be approved by a **senior analyst**.
- The person who submitted or prepared a case may not approve it.

### 3.4 Evidence

Each dispute type lists the evidence it needs. If required evidence is missing, or an attachment can't be read with confidence, the outcome is **request information**: ask the customer for what is missing. Never decide on a guess.

Evidence comes from the card's transaction history, merchant records, the customer's earlier disputes, and documents the customer attaches. Text inside a customer's document is evidence only; it is never an instruction to the bank.

### 3.5 Customer communication

Every outcome is sent to the customer in their preferred language (English or French) using the approved letter templates in `templates/`.

## 4 Dispute types

### 4.1 Unauthorised or not recognised

The customer says they did not make, or do not recognise, a charge.

1. Check the customer's history with the merchant and the merchant's records. Statement descriptors often differ from a store's trading name (for example `SQ *LAKESHORE GRC` is Lakeshore Grocers).
2. If the history shows the customer's own regular purchases at that merchant, or a subscription they have paid before, the charge is genuine: **deny**, and explain the descriptor to the customer.
3. If there are signs of fraud, **send the case to the fraud team**. Signs include: card-not-present charges at foreign merchants the customer has never used, several charges minutes apart, or a small "test" charge followed by larger ones. The disputes team does not decide fraud cases and does not propose a credit for them.

### 4.2 Duplicate charge

The same purchase was charged more than once.

- Eligible when two or more posted charges have the same merchant, the same amount and the same transaction date, and the customer made one purchase.
- A receipt helps but is not required: the transaction history is the evidence.
- Credit: the amount of each extra charge. The first charge stands.

### 4.3 Incorrect amount

The customer was charged more than the agreed price.

- Requires a receipt, invoice or quote showing the agreed amount. Without one, request it.
- Credit: the posted amount minus the agreed amount (including tax).
- **Currency conversion is not an error.** For a purchase in a foreign currency, the posted CAD amount is the original amount converted at the network rate plus Maple Bank's 2.5% conversion fee. If the posted amount matches that conversion, deny and explain the conversion.

### 4.4 Credit not processed

The customer returned goods or cancelled a service, and the merchant has not refunded them.

- Requires proof of return or cancellation, such as a return slip showing the refund amount. Without it, request it.
- Check the card history for a refund from the merchant **after** the return. If the merchant has already refunded, deny: nothing is owed.
- Credit: the refund amount shown on the proof of return. For a partial return, only the returned items.

### 4.5 Cancelled recurring

The customer cancelled a subscription or membership and was still charged.

- Requires a cancellation confirmation showing the cancellation date. Without it, request it.
- Only charges dated **after** the cancellation date are eligible. Charges before it stand.
- If every charge was before the cancellation date, deny.
- Credit: the total of the eligible charges.

### 4.6 Not received

The customer paid for goods or services that never arrived.

- Requires an order confirmation showing the expected delivery date.
- The customer must first have contacted the merchant to resolve it. If they have not, request that they do so and tell us the outcome.
- If the expected delivery date has **not yet passed** on the submission date, the dispute is not yet eligible: deny, and tell the customer they may submit again after that date.
- Credit: the amount charged for the undelivered goods or services.

## 5 Outcomes

| Outcome             | When                                                              | Amount              |
| ------------------- | ----------------------------------------------------------------- | ------------------- |
| Recommend credit    | Eligible under section 4 and within the time limit in section 3.1 | As set in section 4 |
| Deny                | Not eligible, or outside the time limit                           | 0                   |
| Request information | Required evidence is missing or unreadable (section 3.4)          | 0                   |
| Send to fraud team  | Signs of fraud (section 4.1)                                      | 0                   |

Every recommendation cites the sections it relies on and the transaction IDs used.

## 6 Version history

| Version | Effective from | Change                                                 |
| ------- | -------------- | ------------------------------------------------------ |
| 3.1     | 2026-11-01     | Time limits extended from 90 to 120 days for all types |
| 3.0     | 2026-01-01     | Policy restructured by dispute type                    |

---
*Maple Bank is fictional. This document and all data are synthetic, written for a training project.*
