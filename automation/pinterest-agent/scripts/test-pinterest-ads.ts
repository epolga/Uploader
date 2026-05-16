/**
 * test-pinterest-ads.ts
 *
 * Calls GET https://api.pinterest.com/v5/ad_accounts
 * and prints ad account ID, name, country, currency, and status.
 *
 * Reads access token from:
 *   1. Token store  (D:\Tmp\pinterest_tokens.json)
 *   2. Fallback:    Uploader/App.private.config
 *
 * Run:
 *   npx tsx automation/pinterest-agent/scripts/test-pinterest-ads.ts
 */

import { readFileSync, existsSync } from "fs";
import { resolve } from "path";

// ── Token loading ───────────────────────────────────────────────────────────

const TOKEN_STORE_PATH = resolve(__dirname, "..", "..", "..", "secrets", "pinterest_tokens.json");
const PRIVATE_CONFIG_PATH = resolve(
  __dirname,
  "..",
  "..",
  "..",
  "Uploader",
  "App.private.config"
);

function loadTokenFromStore(): string | null {
  if (!existsSync(TOKEN_STORE_PATH)) return null;
  try {
    const data = JSON.parse(readFileSync(TOKEN_STORE_PATH, "utf-8"));
    return data.access_token || null;
  } catch {
    return null;
  }
}

function loadTokenFromXml(): string | null {
  if (!existsSync(PRIVATE_CONFIG_PATH)) return null;
  try {
    const xml = readFileSync(PRIVATE_CONFIG_PATH, "utf-8");
    const match = xml.match(
      /key\s*=\s*"PinterestAccessToken"\s+value\s*=\s*"([^"]+)"/
    );
    return match?.[1] || null;
  } catch {
    return null;
  }
}

function getAccessToken(): string {
  const token = loadTokenFromStore() ?? loadTokenFromXml();
  if (!token) {
    console.error(
      "No Pinterest access token found.\n" +
        `  Checked: ${TOKEN_STORE_PATH}\n` +
        `  Checked: ${PRIVATE_CONFIG_PATH}`
    );
    process.exit(1);
  }
  console.log(`Token loaded (ends …${token.slice(-4)})`);
  return token;
}

// ── API call ────────────────────────────────────────────────────────────────

const ADS_URL = "https://api.pinterest.com/v5/ad_accounts";

async function testAdAccounts(token: string) {
  console.log(`\nGET ${ADS_URL}\n`);

  const res = await fetch(ADS_URL, {
    headers: { Authorization: `Bearer ${token}` },
  });

  const body = await res.text();

  if (res.ok) {
    const data = JSON.parse(body);
    const items: any[] = data.items ?? [];
    console.log(`Success – HTTP ${res.status}`);
    if (items.length === 0) {
      console.log("No ad accounts found.");
    } else {
      console.log(`${items.length} ad account(s):\n`);
      for (const acct of items) {
        console.log(`  Ad Account ID : ${acct.id}`);
        console.log(`  Name          : ${acct.name ?? "(none)"}`);
        console.log(`  Country       : ${acct.country ?? "?"}`);
        console.log(`  Currency      : ${acct.currency ?? "?"}`);
        console.log(`  Status        : ${acct.status ?? "?"}`);
        console.log();
      }
    }
  } else {
    console.error(`Failed – HTTP ${res.status} ${res.statusText}`);
    try {
      const err = JSON.parse(body);
      console.error(`  code:    ${err.code ?? "?"}`);
      console.error(`  message: ${err.message ?? body}`);
    } catch {
      console.error(`  body: ${body}`);
    }
  }
}

// ── Main ────────────────────────────────────────────────────────────────────

const token = getAccessToken();
testAdAccounts(token);
