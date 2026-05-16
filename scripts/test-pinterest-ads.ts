/**
 * test-pinterest-ads.ts
 *
 * Probes whether the current Pinterest OAuth token has Ads API access.
 *
 * Reads the access token from:
 *   1. Token store JSON  (D:\Tmp\pinterest_tokens.json  →  access_token)
 *   2. Fallback: App.private.config XML  (PinterestAccessToken)
 *
 * Calls:  GET https://api.pinterest.com/v5/ad_accounts
 *
 * Run:
 *   npx tsx scripts/test-pinterest-ads.ts
 */

import { readFileSync, existsSync } from "fs";
import { resolve } from "path";

// ── locate token ────────────────────────────────────────────────────────────

const TOKEN_STORE_PATH = resolve(__dirname, "..", "secrets", "pinterest_tokens.json");
const PRIVATE_CONFIG_PATH = resolve(
  __dirname,
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
  // Show only the last 4 chars so we can confirm which token is in use.
  console.log(`Token loaded (ends …${token.slice(-4)})`);
  return token;
}

// ── call Ads API ────────────────────────────────────────────────────────────

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
    console.log(`✔  Success – HTTP ${res.status}`);
    if (items.length === 0) {
      console.log("   No ad accounts found (empty list).");
    } else {
      console.log(`   ${items.length} ad account(s):\n`);
      for (const acct of items) {
        console.log(`   • id: ${acct.id}`);
        console.log(`     name: ${acct.name ?? "(none)"}`);
        console.log(`     currency: ${acct.currency ?? "?"}`);
        console.log(`     status: ${acct.status ?? "?"}\n`);
      }
    }
    console.log(
      "Your current token CAN access the Ads API.\n" +
        "No additional scopes needed for ad-account listing."
    );
  } else {
    console.error(`✘  Failed – HTTP ${res.status} ${res.statusText}`);
    try {
      const err = JSON.parse(body);
      console.error(`   code:    ${err.code ?? "?"}`);
      console.error(`   message: ${err.message ?? body}`);
    } catch {
      console.error(`   body: ${body}`);
    }

    if (res.status === 403) {
      console.error(
        "\n→ 403 means the token's OAuth scopes do not include ads access.\n" +
          "  You need to add scopes such as:\n" +
          "    ads:read\n" +
          "  then re-authorize the app to obtain a new token."
      );
    } else if (res.status === 401) {
      console.error(
        "\n→ 401 means the access token is invalid or expired.\n" +
          "  Refresh it or re-authorize."
      );
    }
  }
}

// ── main ────────────────────────────────────────────────────────────────────

const token = getAccessToken();
testAdAccounts(token);
