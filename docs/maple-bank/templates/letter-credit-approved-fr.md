---
title: Lettre : contestation acceptée (français)
doc_type: template
dispute_types: [duplicate_charge, incorrect_amount, credit_not_processed, cancelled_recurring, not_received, unauthorised]
version: "2.0"
effective_from: 2026-06-01
effective_to:
owner: disputes-ops
approved_by: head-of-disputes
audience: [analyst, senior_analyst]
language: fr
---

# Lettre : contestation acceptée (français)

Placeholders in double braces are filled in by the case system when the letter is sent.

```text
Bonjour {{customer_first_name}},

Nous avons examiné votre contestation concernant le montant de {{disputed_amount}} facturé par {{merchant_name}} le {{transaction_date}} (dossier {{case_id}}).

Bonne nouvelle : nous avons crédité {{amount}} sur votre carte Banque Maple se terminant par {{card_last4}}. Le crédit apparaîtra dans votre compte d'ici 2 jours ouvrables.

Le commerçant pourrait encore nous transmettre des renseignements. Le cas échéant, nous vous écrirons avant d'apporter toute modification.

Merci de faire affaire avec la Banque Maple.
Service des contestations de cartes, Banque Maple
```

---
*Maple Bank is fictional. This document is synthetic, written for a training project.*
