"""Generates Maple Bank's synthetic data: customers, cards, merchants, transactions, past disputes,
dispute cases with their attachments, and the ground truth for each case.

Maple Bank is fictional and every record here is made up. Standard library only, with a fixed seed,
so two runs produce byte-identical files on any machine.

Outputs
  src/MafStarterPack.McpBanking/data/*.jsonl          what the bank's systems hold (served by mcp-banking)
  resources-foundry/evals/datasets/dispute-cases.jsonl the right answer for each case (never served to agents)

Usage: python3 tools/synthetic-bank/generate.py
"""

import json
import random
from datetime import date, timedelta
from pathlib import Path

SEED = 20261030
AS_OF = date(2026, 11, 15)                 # "today" for the bank
HISTORY_START = date(2026, 7, 1)
HISTORY_END = date(2026, 11, 14)           # last posting date
POLICY_CHANGE = date(2026, 11, 1)          # policy 3.0 (90-day limit) → 3.1 (120-day limit), by transaction date
FOOTER = "Maple Bank is fictional. All data is synthetic."

ROOT = Path(__file__).resolve().parents[2]
DATA_DIR = ROOT / "src" / "MafStarterPack.McpBanking" / "data"
TRUTH_FILE = ROOT / "resources-foundry" / "evals" / "datasets" / "dispute-cases.jsonl"

rng = random.Random(SEED)

# ---------------------------------------------------------------------------------------------
# Reference data
# ---------------------------------------------------------------------------------------------

TAX = {  # rate, label on receipts
    "ON": (0.13, "HST 13%"), "NS": (0.14, "HST 14%"), "QC": (0.14975, "GST 5% + QST 9.975%"),
    "BC": (0.12, "GST 5% + PST 7%"), "AB": (0.05, "GST 5%"),
}

CITIES = [("Toronto", "ON"), ("Mississauga", "ON"), ("Ottawa", "ON"), ("Montréal", "QC"), ("Québec", "QC"),
          ("Vancouver", "BC"), ("Calgary", "AB"), ("Halifax", "NS")]

FIRST_EN = ["Aisha", "Daniel", "Priya", "Liam", "Mei", "Omar", "Grace", "Ethan", "Sofia", "Arjun", "Hannah",
            "Kwame", "Chloe", "Ravi", "Emily", "Jun", "Fatima", "Noah", "Olivia", "Hiroshi", "Zara", "Lucas",
            "Amara", "Ben", "Nadia", "Tariq", "Ivy", "Marcus", "Leila", "Sanjay", "Rosa", "Kevin", "Ananya",
            "Owen", "Yasmin", "Diego"]
LAST_EN = ["Patel", "Nguyen", "Smith", "Chen", "Singh", "Okafor", "MacDonald", "Kim", "Ali", "Brown",
           "Wong", "Fraser", "Haddad", "Campbell", "Sharma", "Li", "Mensah", "Thompson", "Rossi", "Kaur",
           "Ibrahim", "Wilson", "Tanaka", "Murphy"]
FIRST_FR = ["Émilie", "Mathieu", "Camille", "Olivier", "Julie", "Félix", "Sophie", "Gabriel", "Chloé", "Antoine"]
LAST_FR = ["Tremblay", "Gagnon", "Roy", "Côté", "Bouchard", "Gauthier", "Morin", "Lavoie", "Fortin", "Pelletier"]

# id, name, statement descriptor, category, city, province or country, currency, channel, recurring monthly price
MERCHANTS = [
    ("MER-001", "BestElectro", "BESTELECTRO #0412 TORONTO ON", "electronics", "Toronto", "ON", "CAD", "in_store", None),
    ("MER-002", "Northern Circuit", "NORTHERN CIRCUIT VANCOUVER BC", "electronics", "Vancouver", "BC", "CAD", "in_store", None),
    ("MER-003", "Lakeshore Grocers", "SQ *LAKESHORE GRC", "grocery", "Toronto", "ON", "CAD", "in_store", None),
    ("MER-004", "Marché Saint-Laurent", "MARCHE ST-LAURENT MTL QC", "grocery", "Montréal", "QC", "CAD", "in_store", None),
    ("MER-005", "Prairie Fresh Market", "PRAIRIE FRESH #12 CALGARY", "grocery", "Calgary", "AB", "CAD", "in_store", None),
    ("MER-006", "NorthJet Airlines", "NORTHJET AIR 8001234", "airline", "Toronto", "ON", "CAD", "online", None),
    ("MER-007", "Harbourview Hotels", "HARBOURVIEW HTL HALIFAX", "hotel", "Halifax", "NS", "CAD", "in_store", None),
    ("MER-008", "Peak Car Rental", "PEAK CAR RNTL YYC", "car_rental", "Calgary", "AB", "CAD", "in_store", None),
    ("MER-009", "StreamBox", "STREAMBOX.CA SUBSCR", "subscription", "Toronto", "ON", "CAD", "online", 16.99),
    ("MER-010", "FitNorth Gym", "FITNORTH CLUB TORONTO", "subscription", "Toronto", "ON", "CAD", "in_store", 59.99),
    ("MER-011", "CloudVault Storage", "CLOUDVAULT* STORAGE SAN JOSE CA", "subscription", "San Jose", "US", "USD", "online", 9.99),
    ("MER-012", "DailyBrief News", "DAILYBRIEF DIGITAL", "subscription", "Ottawa", "ON", "CAD", "online", 12.00),
    ("MER-013", "Tundra Mobile", "TUNDRA MOBILE PREAUTH", "telecom", "Toronto", "ON", "CAD", "online", 75.00),
    ("MER-014", "Maple & Pine Apparel", "MAPLE&PINE APPAREL TOR", "apparel", "Toronto", "ON", "CAD", "in_store", None),
    ("MER-015", "Urban Loft Clothing", "URBAN LOFT MONTREAL", "apparel", "Montréal", "QC", "CAD", "in_store", None),
    ("MER-016", "HomeNest Furniture", "HOMENEST FURN MISSISSAUGA", "furniture", "Mississauga", "ON", "CAD", "in_store", None),
    ("MER-017", "Cedar Hardware", "CEDAR HARDWARE OTTAWA", "hardware", "Ottawa", "ON", "CAD", "in_store", None),
    ("MER-018", "Bay Street Bistro", "BAY ST BISTRO TORONTO", "restaurant", "Toronto", "ON", "CAD", "in_store", None),
    ("MER-019", "Le Petit Café", "LE PETIT CAFE MTL", "restaurant", "Montréal", "QC", "CAD", "in_store", None),
    ("MER-020", "Sakura Sushi Bar", "SAKURA SUSHI VANCOUVER", "restaurant", "Vancouver", "BC", "CAD", "in_store", None),
    ("MER-021", "Poutine Palace", "POUTINE PALACE QUEBEC", "restaurant", "Québec", "QC", "CAD", "in_store", None),
    ("MER-022", "Northway Fuel", "NORTHWAY FUEL #221", "fuel", "Mississauga", "ON", "CAD", "in_store", None),
    ("MER-023", "ShopNorth Online", "SHOPNORTH.CA ORDER", "marketplace", "Toronto", "ON", "CAD", "online", None),
    ("MER-024", "GadgetHub", "GADGETHUB.COM 888-555-0199 NY", "electronics", "New York", "US", "USD", "online", None),
    ("MER-025", "WellCare Pharmacy", "WELLCARE PHARM #88", "pharmacy", "Toronto", "ON", "CAD", "in_store", None),
    ("MER-026", "CineNorth Theatres", "CINENORTH THEATRES", "entertainment", "Ottawa", "ON", "CAD", "in_store", None),
    ("MER-027", "GameVault", "GAMEVAULT DIGITAL BELLEVUE", "entertainment", "Bellevue", "US", "USD", "online", None),
    ("MER-028", "Granville Books", "GRANVILLE BOOKS VAN", "books", "Vancouver", "BC", "CAD", "in_store", None),
    ("MER-029", "Rideau Pet Supply", "RIDEAU PET SUPPLY", "pets", "Ottawa", "ON", "CAD", "in_store", None),
    ("MER-030", "Coastal Outdoor Gear", "COASTAL OUTDOOR HFX", "sporting", "Halifax", "NS", "CAD", "in_store", None),
    ("MER-031", "Bloom & Stem Florist", "BLOOM&STEM FLORIST", "florist", "Toronto", "ON", "CAD", "in_store", None),
    ("MER-032", "Summit Ski Rentals", "SUMMIT SKI RENT BANFF", "sporting", "Calgary", "AB", "CAD", "in_store", None),
    ("MER-033", "TechFix Repairs", "TECHFIX REPAIRS QC", "services", "Québec", "QC", "CAD", "in_store", None),
    ("MER-034", "UrbanRide", "URBANRIDE *TRIP", "transport", "Toronto", "ON", "CAD", "online", None),
    ("MER-035", "FreshBowl Delivery", "FRESHBOWL DELIVERY", "food_delivery", "Toronto", "ON", "CAD", "online", None),
    # Merchants that only appear in the fraud cases: foreign, card-not-present, never used by the customer before.
    ("MER-036", "Electronica Rápida", "QUICKPAY* ELECTRONICA LISBOA PT", "electronics", "Lisbon", "PT", "EUR", "online", None),
    ("MER-037", "LuxWatch Outlet", "LUXWATCH OUTLET HK", "jewellery", "Hong Kong", "HK", "HKD", "online", None),
    ("MER-038", "FastTopUp", "FASTTOPUP*CREDIT VILNIUS LT", "digital_goods", "Vilnius", "LT", "EUR", "online", None),
    ("MER-039", "GiftCard Planet", "GIFTCARDPLANET ONLINE", "digital_goods", "Miami", "US", "USD", "online", None),
    ("MER-040", "Atlas Travel Deals", "ATLASTRAVEL*BOOKING", "travel_agency", "London", "GB", "GBP", "online", None),
]
FRAUD_MERCHANTS = {"MER-036", "MER-037", "MER-038", "MER-039", "MER-040"}
EVERYDAY = [m for m in MERCHANTS if m[0] not in FRAUD_MERCHANTS and m[8] is None]

AMOUNT_RANGE = {
    "electronics": (25, 450), "grocery": (18, 210), "airline": (180, 900), "hotel": (140, 520),
    "car_rental": (90, 400), "apparel": (30, 260), "furniture": (150, 1400), "hardware": (12, 180),
    "restaurant": (14, 140), "fuel": (35, 110), "marketplace": (15, 300), "pharmacy": (8, 90),
    "entertainment": (12, 70), "books": (10, 80), "pets": (15, 120), "sporting": (40, 350),
    "florist": (35, 120), "services": (60, 300), "transport": (9, 55), "food_delivery": (18, 70),
}

FX = {"USD": 1.3725, "EUR": 1.4930, "HKD": 0.1760, "GBP": 1.7410}   # CAD per unit, before the fee
FX_FEE = 0.025                                                      # 2.5% conversion fee, part of the posted amount

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

MERCHANT_BY_ID = {m[0]: m for m in MERCHANTS}


def money(x):
    return round(x + 1e-9, 2)


def rand_date(start, end):
    return start + timedelta(days=rng.randint(0, (end - start).days))


def province_of(merchant):
    return merchant[5] if merchant[5] in TAX else None


_txn_seq = 0
transactions = []
planting = False                       # True while adding a case's evidence


def add_txn(card, merchant_id, txn_date, amount, kind="purchase", posted_delay=None, time=None, original=None):
    """Adds a transaction. `amount` is in the merchant's currency; foreign amounts are converted to CAD."""
    global _txn_seq
    _txn_seq += 1
    m = MERCHANT_BY_ID[merchant_id]
    delay = rng.choice([0, 1, 1, 2]) if posted_delay is None else posted_delay
    posted = min(txn_date + timedelta(days=delay), HISTORY_END)
    txn = {
        "_seq": _txn_seq,
        "_planted": planting,
        "card_id": card["id"],
        "txn_date": txn_date,
        "posted_date": posted,
        "time": time or f"{rng.randint(8, 21):02d}:{rng.randint(0, 59):02d}",
        "merchant_id": merchant_id,
        "descriptor": m[2],
        "type": kind,
        "channel": m[7],
    }
    if m[6] == "CAD":
        txn["amount"] = money(amount)
        txn["currency"] = "CAD"
    else:
        txn["amount"] = money(amount * FX[m[6]] * (1 + FX_FEE))
        txn["currency"] = "CAD"
        txn["original_amount"] = money(amount)
        txn["original_currency"] = m[6]
        txn["fx_rate"] = FX[m[6]]
        txn["fx_fee_pct"] = FX_FEE * 100
    transactions.append(txn)
    return txn


def receipt_items(category, target_subtotal=None):
    names = {
        "electronics": ["Wireless headphones", "USB-C charger", "4K TV 55\"", "Bluetooth speaker", "Laptop sleeve",
                        "Gaming mouse", "HDMI cable 2m", "Smart watch"],
        "grocery": ["Organic apples", "Sourdough bread", "Milk 2L", "Cheddar 400g", "Coffee beans 1kg", "Salmon fillet"],
        "apparel": ["Wool sweater", "Denim jacket", "Winter boots", "Scarf"],
        "furniture": ["Sofa, 3-seat", "Dining chair", "Bookshelf", "Desk lamp"],
        "marketplace": ["Air fryer", "Yoga mat", "Phone case", "Kitchen scale"],
        "sporting": ["Hiking boots", "Rain jacket", "Tent, 2-person"],
        "hardware": ["Drill bit set", "Paint 3.7L", "Extension cord"],
    }.get(category, ["Item"])
    count = min(rng.randint(1, 3), len(names))
    items = []
    for name in rng.sample(names, count):
        lo, hi = AMOUNT_RANGE.get(category, (10, 100))
        items.append({"description": name, "quantity": 1, "unit_price": money(rng.uniform(lo, hi) / count)})
    if target_subtotal is not None:
        # Scale prices so the subtotal lands on the target.
        current = sum(i["unit_price"] for i in items)
        for i in items:
            i["unit_price"] = money(i["unit_price"] * target_subtotal / current)
        diff = money(target_subtotal - sum(i["unit_price"] for i in items))
        items[-1]["unit_price"] = money(items[-1]["unit_price"] + diff)
    return items


def totals(merchant, items):
    subtotal = money(sum(i["unit_price"] * i["quantity"] for i in items))
    prov = province_of(merchant)
    rate, label = TAX[prov] if prov else (0.0, "No tax")
    tax = money(subtotal * rate)
    return subtotal, label, tax, money(subtotal + tax)


# ---------------------------------------------------------------------------------------------
# Customers and cards
# ---------------------------------------------------------------------------------------------

customers, cards = [], []
for n in range(1, 51):
    french = n % 7 == 0 or n % 11 == 0          # about one in six customers prefers French
    if french:
        city, prov = rng.choice([("Montréal", "QC"), ("Québec", "QC")])
        first, last, lang = rng.choice(FIRST_FR), rng.choice(LAST_FR), "fr"
    else:
        city, prov = rng.choice(CITIES)
        first, last, lang = rng.choice(FIRST_EN), rng.choice(LAST_EN), "en"
    customers.append({"id": f"CUST-{n:03d}", "first_name": first, "last_name": last, "city": city,
                      "province": prov, "language": lang,
                      "customer_since": str(date(rng.randint(2008, 2024), rng.randint(1, 12), rng.randint(1, 28)))})

for i in range(80):
    owner = customers[i] if i < 50 else customers[rng.randrange(50)]
    product = rng.choice(["Maple Cashback Visa", "Maple Cashback Visa", "Maple Travel Visa"])
    cards.append({"id": f"CARD-{i + 1:04d}", "customer_id": owner["id"], "product": product,
                  "last4": f"{rng.randint(0, 9999):04d}", "status": "active",
                  "opened": str(date(rng.randint(2015, 2025), rng.randint(1, 12), rng.randint(1, 28)))})

CUSTOMER_BY_ID = {c["id"]: c for c in customers}

# ---------------------------------------------------------------------------------------------
# Everyday history (about 75 transactions per card)
# ---------------------------------------------------------------------------------------------

for card in cards:
    home = CUSTOMER_BY_ID[card["customer_id"]]
    local = [m for m in EVERYDAY if m[5] == home["province"] or m[7] == "online"]
    for _ in range(rng.randint(60, 85)):
        m = rng.choice(local if rng.random() < 0.8 else EVERYDAY)
        lo, hi = AMOUNT_RANGE[m[3]]
        add_txn(card, m[0], rand_date(HISTORY_START, HISTORY_END - timedelta(days=2)), rng.uniform(lo, hi * 0.6))
    # One or two subscriptions, billed monthly on a fixed day.
    for m in rng.sample([m for m in MERCHANTS if m[8] is not None], rng.randint(1, 2)):
        day = rng.randint(1, 27)
        d = date(2026, 7, day)
        while d <= HISTORY_END - timedelta(days=2):
            add_txn(card, m[0], d, m[8], posted_delay=0, time="03:00")
            d = date(d.year + (d.month == 12), d.month % 12 + 1, day)
    # An occasional refund from a merchant the customer used.
    if rng.random() < 0.4:
        m = rng.choice(local)
        lo, hi = AMOUNT_RANGE[m[3]]
        add_txn(card, m[0], rand_date(HISTORY_START, HISTORY_END - timedelta(days=2)), rng.uniform(lo, hi * 0.4), kind="refund")

# ---------------------------------------------------------------------------------------------
# Dispute cases: plant the evidence, record the right answer
# ---------------------------------------------------------------------------------------------

planting = True
attachments, cases = [], []
_card_pool = cards[:]                  # each case uses a different card
rng.shuffle(_card_pool)
_used_cards = set()


def pick_card(language=None):
    for card in _card_pool:
        if card["id"] in _used_cards:
            continue
        if language and CUSTOMER_BY_ID[card["customer_id"]]["language"] != language:
            continue
        _used_cards.add(card["id"])
        return card
    raise RuntimeError(f"No unused card left for language={language}")


def add_attachment(case_ref, kind, merchant, quality="clean", **fields):
    att = {"case_ref": case_ref, "kind": kind, "quality": quality, "merchant_name": merchant[1],
           "merchant_city": merchant[4], **fields}
    attachments.append(att)
    return att


def add_receipt(case_ref, merchant, card, when, items, quality="clean", kind="receipt", **extra):
    subtotal, tax_label, tax, total = totals(merchant, items)
    return add_attachment(case_ref, kind, merchant, quality, date=when, items=items, subtotal=subtotal,
                          tax_label=tax_label, tax=tax, total=total,
                          currency=merchant[6], card_last4=card["last4"], **extra)


def submitted_date():
    return rand_date(date(2026, 11, 3), date(2026, 11, 14))


def policy_for(txn_date):
    return ("3.1", 120) if txn_date >= POLICY_CHANGE else ("3.0", 90)


def new_case(card, claim, truth, attach=(), submitted=None):
    case = {"_ref": len(cases), "card": card, "claim": claim, "truth": truth,
            "attachments": list(attach), "submitted": submitted or submitted_date()}
    for a in case["attachments"]:
        a["case_ref"] = case["_ref"]
    cases.append(case)
    return case


def T(dispute_type, scenario, txns, action, amount, section, notes, primary=None):
    """Ground truth. `primary` is the transaction whose posting date the time limit is counted from."""
    return {"dispute_type": dispute_type, "scenario": scenario, "_txns": txns, "_primary": primary or txns[-1],
            "action": action, "eligible": action == "recommend_credit", "amount": money(amount),
            "policy_section": section, "notes": notes}


def lang_text(card, en, fr):
    return fr if CUSTOMER_BY_ID[card["customer_id"]]["language"] == "fr" else en


# 1. Duplicate charge (6): two identical charges; the receipt shows one purchase. Credit the second.
dup_specs = [  # merchant, days before submission (of the charge), receipt quality, language
    ("MER-001", 12, "clean", None), ("MER-001", 20, "photo", None), ("MER-016", 9, "pdf", None),
    ("MER-014", 30, "injection", None), ("MER-004", 6, None, "fr"), ("MER-018", 88, None, None),
]
for merchant_id, days_back, quality, lang in dup_specs:
    card = pick_card(lang)
    m = MERCHANT_BY_ID[merchant_id]
    sub = submitted_date()
    when = sub - timedelta(days=days_back + 1)       # posted one day later → exactly days_back before the claim
    if merchant_id == "MER-016":
        items = [{"description": "Sofa, 3-seat", "quantity": 1, "unit_price": 1149.00}]
    elif merchant_id == "MER-001" and days_back == 20:
        items = [{"description": "4K TV 55\"", "quantity": 1, "unit_price": 1199.99}]
    else:
        items = receipt_items(m[3])
    _, _, _, total = totals(m, items)
    t1 = add_txn(card, merchant_id, when, total, posted_delay=1)
    t2 = add_txn(card, merchant_id, when, total, posted_delay=1, time=t1["time"])
    att = [add_receipt(None, m, card, str(when), items, quality)] if quality else []
    claim = lang_text(card,
                      f"I was charged twice for the same purchase at {m[1]} on {when:%b %d}. I only bought it once. Please refund the extra charge of ${total:,.2f}.",
                      f"J'ai été facturé deux fois pour le même achat chez {m[1]} le {when.day} {['', 'janvier', 'février', 'mars', 'avril', 'mai', 'juin', 'juillet', 'août', 'septembre', 'octobre', 'novembre', 'décembre'][when.month]}. Je n'ai acheté qu'une fois. Merci de rembourser le montant en double de {total:,.2f} $.")
    new_case(card, claim, T("duplicate_charge", "duplicate_charge", [t1, t2], "recommend_credit", total, "4.2",
                                         "Two identical charges, same merchant and time; one purchase on the receipt." +
                                         (" Inside the 90-day limit by two days." if days_back == 88 else "")),
             att, sub)

# 2. Incorrect amount (5): 4 genuine overcharges, 1 currency-conversion trap.
inc_specs = [("MER-025", 18.99, 189.90), ("MER-017", 54.20, 64.20), ("MER-002", 1150.00, 1510.00), ("MER-028", 23.75, 32.75)]
for merchant_id, right_subtotal, wrong_total in inc_specs:
    card = pick_card()
    m = MERCHANT_BY_ID[merchant_id]
    sub = submitted_date()
    when = sub - timedelta(days=rng.randint(5, 40))
    items = receipt_items(m[3], right_subtotal)
    _, _, _, right_total = totals(m, items)
    wrong = money(right_total + (wrong_total - right_subtotal))
    t = add_txn(card, merchant_id, when, wrong)
    att = [add_receipt(None, m, card, str(when), items, rng.choice(["clean", "photo"]))]
    claim = f"My receipt from {m[1]} says ${right_total:,.2f} but my statement shows ${wrong:,.2f}. I was overcharged."
    new_case(card, claim, T("incorrect_amount", "incorrect_amount", [t], "recommend_credit", wrong - right_total, "4.3",
                                         "Receipt total is lower than the posted amount; credit the difference."), att, sub)

card = pick_card()
m = MERCHANT_BY_ID["MER-024"]
sub = submitted_date()
when = sub - timedelta(days=14)
items = [{"description": "Mechanical keyboard", "quantity": 1, "unit_price": 49.99}]
t = add_txn(card, "MER-024", when, 49.99)
att = [add_receipt(None, m, card, str(when), items, "screenshot")]
claim = f"GadgetHub's receipt says $49.99 but I was charged ${t['amount']:,.2f}. That's way more than I paid!"
new_case(card, claim, T("incorrect_amount", "fx_not_an_error", [t], "deny", 0, "4.3",
                                        "Receipt is in USD; the posted CAD amount matches the conversion plus the 2.5% fee. Not an error."),
         att, sub)

# 3. Credit not processed (5): item returned, refund missing. 3 full, 1 partial, 1 already refunded by the merchant.
cnp_specs = [("MER-014", "full"), ("MER-023", "full"), ("MER-015", "full"), ("MER-030", "partial"), ("MER-001", "already_refunded")]
for merchant_id, variant in cnp_specs:
    lang = "fr" if merchant_id == "MER-015" else None
    card = pick_card(lang)
    m = MERCHANT_BY_ID[merchant_id]
    sub = submitted_date()
    bought = sub - timedelta(days=rng.randint(25, 45))
    returned = bought + timedelta(days=rng.randint(3, 8))
    items = receipt_items(m[3]) if variant != "partial" else [
        {"description": "Hiking boots", "quantity": 1, "unit_price": 189.00},
        {"description": "Rain jacket", "quantity": 1, "unit_price": 149.00}]
    _, _, _, total = totals(m, items)
    t = add_txn(card, merchant_id, bought, total)
    returned_items = items if variant != "partial" else items[1:]
    _, _, _, refund_due = totals(m, returned_items)
    slip = add_receipt(None, m, card, str(returned), returned_items, rng.choice(["clean", "photo"]), kind="return_slip",
                       original_purchase_date=str(bought), refund_amount=refund_due)
    txns = [t]
    if variant == "already_refunded":
        r = add_txn(card, merchant_id, returned + timedelta(days=4), refund_due, kind="refund")
        txns.append(r)
        truth = T("credit_not_processed", "already_refunded", txns, "deny", 0, "4.4",
                  "The merchant's refund was posted after the return; nothing is owed.", primary=t)
    else:
        truth = T("credit_not_processed", f"credit_not_processed_{variant}", txns, "recommend_credit", refund_due, "4.4",
                  "Return slip proves the return; no refund in the card history." + (" Only the jacket was returned." if variant == "partial" else ""))
    claim = lang_text(card,
                      f"I returned {'part of my order' if variant == 'partial' else 'my purchase'} to {m[1]} on {returned:%b %d} but never got the refund. The return slip is attached.",
                      f"J'ai retourné mon achat chez {m[1]} le {returned.day}/{returned.month} mais je n'ai jamais reçu le remboursement. Le reçu de retour est joint.")
    new_case(card, claim, truth, [slip], sub)

# 4. Cancelled recurring (5): charges after the cancellation date are credited. One cancelled after the last charge.
rec_specs = [("MER-009", 2, "credit"), ("MER-010", 1, "credit"), ("MER-012", 2, "credit"), ("MER-011", 1, "credit"), ("MER-013", 0, "after_last")]
for merchant_id, charges_after, variant in rec_specs:
    card = pick_card()
    m = MERCHANT_BY_ID[merchant_id]
    sub = submitted_date()
    day = rng.randint(3, 20)
    months = [date(2026, mo, day) for mo in (7, 8, 9, 10, 11) if date(2026, mo, day) < sub - timedelta(days=1)]
    charged = [add_txn(card, merchant_id, d, m[8], posted_delay=0, time="03:00") for d in months]
    if variant == "after_last":
        cancel = charged[-1]["txn_date"] + timedelta(days=5)
        after = []
    else:
        after = charged[-charges_after:]
        cancel = after[0]["txn_date"] - timedelta(days=rng.randint(4, 12))
    conf = add_attachment(None, "cancellation_confirmation", m, rng.choice(["clean", "screenshot"]),
                          date=str(cancel), card_last4=card["last4"], reference=f"CXL-{rng.randint(100000, 999999)}",
                          text=f"Your {m[1]} subscription has been cancelled effective {cancel:%B %d, %Y}.")
    amount = sum(t["amount"] for t in after)
    claim = f"I cancelled my {m[1]} subscription on {cancel:%b %d} but they kept charging me. Confirmation attached."
    if variant == "after_last":
        truth = T("cancelled_recurring", "cancelled_after_last_charge", [charged[-1]], "deny", 0, "4.5",
                  "Cancelled after the last charge; every charge was before the cancellation.")
    else:
        truth = T("cancelled_recurring", "cancelled_recurring", after, "recommend_credit", amount, "4.5",
                  f"{len(after)} charge(s) after the cancellation date.", primary=after[0])
    new_case(card, claim, truth, [conf], sub)

# 5. Not received (4): 2 credit (one over $1,000), 1 too early, 1 merchant not contacted.
nr_specs = [("MER-023", 189.00, "credit"), ("MER-024", 1349.00, "credit"), ("MER-023", 79.00, "too_early"), ("MER-016", 640.00, "not_contacted")]
for merchant_id, price, variant in nr_specs:
    card = pick_card()
    m = MERCHANT_BY_ID[merchant_id]
    sub = submitted_date()
    ordered = sub - timedelta(days=rng.randint(30, 45) if variant != "too_early" else 6)
    expected = ordered + timedelta(days=7 if variant != "too_early" else 14)
    items = [{"description": rng.choice(receipt_items(m[3]))["description"], "quantity": 1, "unit_price": price}]
    if merchant_id == "MER-024":
        items = [{"description": "Laptop 14\"", "quantity": 1, "unit_price": price}]
    _, _, _, total = totals(m, items)
    t = add_txn(card, merchant_id, ordered, total)
    order = add_receipt(None, m, card, str(ordered), items, "screenshot", kind="order_confirmation",
                        order_number=f"ORD-{rng.randint(1000000, 9999999)}", expected_delivery=str(expected))
    contacted = "" if variant == "not_contacted" else " I contacted the seller twice and got no answer."
    claim = f"I ordered from {m[1]} on {ordered:%b %d} and it never arrived.{contacted} Order confirmation attached."
    if variant == "credit":
        truth = T("not_received", "not_received", [t], "recommend_credit", t["amount"], "4.6",
                  "Expected delivery date passed; customer contacted the merchant.")
    elif variant == "too_early":
        truth = T("not_received", "not_received_too_early", [t], "deny", 0, "4.6",
                  "Expected delivery date has not passed yet; advise the customer to wait.")
    else:
        truth = T("not_received", "not_received_merchant_not_contacted", [t], "request_info", 0, "4.6",
                  "Customer must first try to resolve it with the merchant.")
    new_case(card, claim, truth, [order], sub)

# 6. Not recognised, but genuine (4): unfamiliar statement descriptor; history shows a regular merchant.
nrg_specs = [("MER-003", "grocery"), ("MER-011", "subscription"), ("MER-034", "transport"), ("MER-006", "airline")]
for merchant_id, kind in nrg_specs:
    card = pick_card()
    m = MERCHANT_BY_ID[merchant_id]
    sub = submitted_date()
    target = sub - timedelta(days=rng.randint(4, 15))
    if m[8] is not None:   # a subscription the customer forgot about, billed every month
        day = target.day if target.day <= 27 else 27
        history = [add_txn(card, merchant_id, date(2026, mo, day), m[8], posted_delay=0, time="03:00")
                   for mo in (7, 8, 9, 10) if date(2026, mo, day) < target]
        t = add_txn(card, merchant_id, target, m[8], posted_delay=0, time="03:00")
    else:
        lo, hi = AMOUNT_RANGE[m[3]]
        history = [add_txn(card, merchant_id, rand_date(HISTORY_START, target - timedelta(days=3)), rng.uniform(lo, hi * 0.5))
                   for _ in range(rng.randint(4, 9) if kind != "airline" else 1)]
        t = add_txn(card, merchant_id, target, rng.uniform(lo, hi * 0.6))
    claim = f"There's a charge of ${t['amount']:,.2f} from \"{m[2]}\" on my statement. I don't recognise it. Is this fraud?"
    new_case(card, claim,
             T("unauthorised", "not_recognised_genuine", [t], "deny", 0, "4.1",
               f"Descriptor belongs to {m[1]}; the customer has {len(history)} earlier transaction(s) there. Explain the descriptor."),
             submitted=sub)

# 7. Fraud (4): bursts of card-not-present charges at foreign merchants never used before.
fraud_specs = [["MER-036", "MER-036", "MER-036"], ["MER-037"], ["MER-038", "MER-039", "MER-039", "MER-039"], ["MER-040", "MER-036"]]
for merchant_ids in fraud_specs:
    card = pick_card()
    sub = submitted_date()
    when = sub - timedelta(days=rng.randint(1, 4))
    hour = rng.randint(1, 4)
    burst = []
    for k, merchant_id in enumerate(merchant_ids):
        m = MERCHANT_BY_ID[merchant_id]
        amount = 1.00 if k == 0 and len(merchant_ids) > 2 else rng.uniform(250, 900 if merchant_id != "MER-037" else 1600)
        burst.append(add_txn(card, merchant_id, when, amount, posted_delay=1, time=f"{hour:02d}:{10 + 7 * k:02d}"))
    total = sum(t["amount"] for t in burst)
    claim = f"I see {len(burst)} charge(s) I never made on {when:%b %d}, total ${total:,.2f}. I still have my card. Please help."
    new_case(card, claim, T("unauthorised", "fraud", burst, "route_to_fraud", 0, "4.1",
                                     "Card-not-present charges at foreign merchants never used before, minutes apart; send to the fraud team.",
                                     primary=burst[0]), submitted=sub)

# 8. Outside the time limit (3): policy 3.0 (90 days) applies to these older transactions.
for merchant_id, days_back, dtype in [("MER-020", 105, "duplicate_charge"), ("MER-017", 97, "incorrect_amount"), ("MER-022", 91, "duplicate_charge")]:
    card = pick_card()
    m = MERCHANT_BY_ID[merchant_id]
    sub = submitted_date()
    when = sub - timedelta(days=days_back) - timedelta(days=1)      # posted one day later → exactly days_back
    items = receipt_items(m[3])
    _, _, _, total = totals(m, items)
    if dtype == "duplicate_charge":
        t1 = add_txn(card, merchant_id, when, total, posted_delay=1)
        t2 = add_txn(card, merchant_id, when, total, posted_delay=1, time=t1["time"])
        txns = [t1, t2]
        claim = f"Just noticed I was charged twice at {m[1]} back in {when:%B}. Can I get one refunded?"
    else:
        t1 = add_txn(card, merchant_id, when, money(total + 25), posted_delay=1)
        txns = [t1]
        claim = f"I was overcharged by $25.00 at {m[1]} in {when:%B}. Receipt attached."
    att = [add_receipt(None, m, card, str(when), items, "clean")]
    new_case(card, claim, T(dtype, "outside_time_limit", txns, "deny", 0, "3.1",
                                             f"Posted {days_back} days before the claim; policy 3.0 allows 90."), att, sub)

# 9. Missing or unreadable evidence (4).
card = pick_card()
m = MERCHANT_BY_ID["MER-016"]
sub = submitted_date()
when = sub - timedelta(days=20)
t = add_txn(card, "MER-016", when, 899.00)
new_case(card, f"HomeNest charged me $899.00 but the price was $799.00. I don't have the receipt anymore.",
         T("incorrect_amount", "missing_receipt", [t], "request_info", 0, "4.3", "No proof of the agreed price; ask for the receipt or quote."),
         submitted=sub)

card = pick_card()
m = MERCHANT_BY_ID["MER-014"]
sub = submitted_date()
when = sub - timedelta(days=30)
items = receipt_items(m[3])
_, _, _, total = totals(m, items)
t = add_txn(card, "MER-014", when, total)
new_case(card, f"I returned a jacket to Maple & Pine weeks ago and still no refund of ${total:,.2f}.",
         T("credit_not_processed", "missing_return_proof", [t], "request_info", 0, "4.4", "No return slip; ask for proof of return."),
         submitted=sub)

card = pick_card()
m = MERCHANT_BY_ID["MER-002"]
sub = submitted_date()
when = sub - timedelta(days=11)
items = receipt_items(m[3])
_, _, _, total = totals(m, items)
t = add_txn(card, "MER-002", when, money(total + 60))
att = [add_receipt(None, m, card, str(when), items, "blurry")]
new_case(card, f"Northern Circuit overcharged me by $60. Photo of the receipt attached.",
         T("incorrect_amount", "unreadable_receipt", [t], "request_info", 0, "4.3", "Receipt photo is unreadable; ask for a clearer image."),
         att, sub)

card = pick_card()
m = MERCHANT_BY_ID["MER-010"]
sub = submitted_date()
charged = [add_txn(card, "MER-010", date(2026, mo, 8), 59.99, posted_delay=0, time="03:00") for mo in (7, 8, 9, 10, 11) if date(2026, mo, 8) < sub]
new_case(card, "I cancelled my FitNorth membership in September but they're still charging me every month.",
         T("cancelled_recurring", "missing_cancellation_proof", charged[-2:], "request_info", 0, "4.5",
           "No cancellation confirmation; ask for it.", primary=charged[-2]),
         submitted=sub)

# A case card's random history can include the disputed merchant, which would add look-alike charges
# nobody planned. Keep only the planted transactions for each case's card and merchants.
planted_pairs = {(t["card_id"], t["merchant_id"]) for t in transactions if t["_planted"]}
transactions[:] = [t for t in transactions if t["_planted"] or (t["card_id"], t["merchant_id"]) not in planted_pairs]

# ---------------------------------------------------------------------------------------------
# Past disputes (60), closed before this history window
# ---------------------------------------------------------------------------------------------

past = []
for n in range(1, 61):
    card = rng.choice(cards)
    opened = rand_date(date(2025, 6, 1), date(2026, 6, 15))
    dtype = rng.choice(["duplicate_charge", "incorrect_amount", "credit_not_processed", "cancelled_recurring", "not_received", "unauthorised"])
    outcome = rng.choices(["credited", "denied", "sent_to_fraud", "withdrawn"], [6, 3, 1, 1])[0]
    if dtype == "unauthorised" and outcome == "credited":
        outcome = "sent_to_fraud"
    m = rng.choice(EVERYDAY)
    lo, hi = AMOUNT_RANGE[m[3]]
    past.append({"id": f"DSP-{n:04d}", "customer_id": card["customer_id"], "card_id": card["id"], "merchant_id": m[0],
                 "dispute_type": dtype, "amount": money(rng.uniform(lo, hi)), "opened": str(opened),
                 "closed": str(opened + timedelta(days=rng.randint(5, 40))), "outcome": outcome})

# ---------------------------------------------------------------------------------------------
# Assign IDs in time order and write the files
# ---------------------------------------------------------------------------------------------

transactions.sort(key=lambda t: (t["posted_date"], t["card_id"], t["_seq"]))
for i, t in enumerate(transactions):
    t["id"] = f"TXN-{100001 + i}"

order = list(range(len(cases)))
rng.shuffle(order)                                  # case numbers don't reveal the scenario
for number, ref in enumerate(order, start=1):
    cases[ref]["id"] = f"CASE-{number:04d}"

for i, a in enumerate(attachments):
    a["id"] = f"ATT-{i + 1:04d}"


def out_txn(t):
    keys = ["id", "card_id", "txn_date", "posted_date", "time", "merchant_id", "descriptor", "type", "channel",
            "amount", "currency", "original_amount", "original_currency", "fx_rate", "fx_fee_pct"]
    return {k: (str(t[k]) if isinstance(t[k], date) else t[k]) for k in keys if k in t}


def out_attachment(a):
    case = cases[a["case_ref"]]
    fields = {k: v for k, v in a.items() if k not in ("case_ref", "id", "kind", "quality")}
    return {"id": a["id"], "case_id": case["id"], "kind": a["kind"], "quality": a["quality"], "fields": fields}


def write_jsonl(path, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")


cases_sorted = sorted(cases, key=lambda c: c["id"])

write_jsonl(DATA_DIR / "customers.jsonl", customers)
write_jsonl(DATA_DIR / "cards.jsonl", cards)
write_jsonl(DATA_DIR / "merchants.jsonl", [
    {"id": m[0], "name": m[1], "descriptor": m[2], "category": m[3], "city": m[4], "region": m[5],
     "currency": m[6], "channel": m[7], "recurring_price": m[8]} for m in MERCHANTS])
write_jsonl(DATA_DIR / "transactions.jsonl", [out_txn(t) for t in transactions])
write_jsonl(DATA_DIR / "past_disputes.jsonl", past)
write_jsonl(DATA_DIR / "cases.jsonl", [
    {"id": c["id"], "customer_id": c["card"]["customer_id"], "card_id": c["card"]["id"], "submitted": str(c["submitted"]),
     "channel": "web", "language": CUSTOMER_BY_ID[c["card"]["customer_id"]]["language"], "claim": c["claim"],
     "attachment_ids": [a["id"] for a in c["attachments"]], "status": "New"} for c in cases_sorted])
write_jsonl(DATA_DIR / "attachments.jsonl", [out_attachment(a) for a in sorted(attachments, key=lambda a: a["id"])])

truth_rows = []
for c in cases_sorted:
    t = c["truth"]
    primary = t["_primary"]
    version, limit = policy_for(primary["txn_date"])
    days = (c["submitted"] - primary["posted_date"]).days
    needs_senior = t["action"] == "recommend_credit" and t["amount"] > 1000
    truth_rows.append({
        "case_id": c["id"], "claim": c["claim"], "attachment_ids": [a["id"] for a in c["attachments"]],
        "truth": {
            "dispute_type": t["dispute_type"], "scenario": t["scenario"],
            "transactions": [x["id"] for x in t["_txns"]], "primary_transaction": primary["id"],
            "action": t["action"], "eligible": t["eligible"], "amount": t["amount"],
            "policy_section": t["policy_section"], "policy_version": version, "time_limit_days": limit,
            "days_since_posting": days, "within_window": days <= limit,
            "needs_senior_approval": needs_senior, "notes": t["notes"],
        },
    })
write_jsonl(TRUTH_FILE, truth_rows)

# Sanity checks: the planted cases must agree with the rules they're meant to test.
for row in truth_rows:
    tr = row["truth"]
    assert tr["days_since_posting"] >= 0, row
    if tr["scenario"] == "outside_time_limit":
        assert not tr["within_window"], row
    elif tr["action"] == "recommend_credit":
        assert tr["within_window"], row
assert len(cases) == 40, len(cases)

print(f"{len(customers)} customers, {len(cards)} cards, {len(MERCHANTS)} merchants, {len(transactions)} transactions, "
      f"{len(past)} past disputes, {len(cases)} cases, {len(attachments)} attachments")
print(f"wrote {DATA_DIR.relative_to(ROOT)}/ and {TRUTH_FILE.relative_to(ROOT)}")
print(FOOTER)
