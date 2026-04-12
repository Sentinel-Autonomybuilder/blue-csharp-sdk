# The Handshake dVPN Manifesto

> *"A VPN without decentralized DNS is a locked door with windows wide open."*

---

## The Missing Layer

The world discovered decentralized VPNs and celebrated. Encrypted tunnels, peer-to-peer bandwidth, no central servers to seize. Sentinel proved it works — 900+ nodes, 90+ countries, real traffic, real privacy.

But there is a fatal blind spot that nobody talks about.

**Every decentralized VPN in existence still depends on centralized DNS.**

When you type a URL, before a single encrypted packet flows through your VPN tunnel, a DNS query fires. That query — your intent to visit a website — goes to a resolver. Google's `8.8.8.8`. Cloudflare's `1.1.1.1`. Your ISP's default. These are centralized services run by corporations under government jurisdiction. They can log, filter, lie, and comply with takedown orders.

Your VPN tunnel is encrypted. Your DNS query is a confession.

A government doesn't need to break your encryption. They just need to see your DNS queries to know every site you visit. An ISP doesn't need to inspect your packets. They just need to poison your DNS to redirect you to a block page. A corporation doesn't need your browsing history. They just need to control the resolver to control what you can reach.

**DNS is the first thing that happens and the last thing anyone secures.** It is the foundation layer of internet access. Without decentralized DNS, decentralized VPN is theater.

---

## Why Handshake

Handshake (HNS) is a decentralized, permissionless naming protocol. It replaces the root DNS zone — the very top of the naming hierarchy — with a blockchain. No ICANN. No registrars. No single entity that can revoke a name, seize a domain, or comply with a censorship order.

| Traditional DNS | Handshake DNS |
|----------------|---------------|
| ICANN controls the root zone | Blockchain controls the root zone |
| Domains can be seized by court order | Names are owned by cryptographic keys |
| Registrars can suspend your domain | No registrar — you ARE the registrar |
| Governments can force DNS filtering | No central point to force |
| TLDs cost millions to apply for | TLDs are auctioned on-chain to anyone |
| Resolution depends on 13 root servers | Resolution is peer-to-peer |

Handshake doesn't replace DNS. It replaces the **trust layer** underneath DNS. The protocol is backwards-compatible — it resolves traditional domains normally while also resolving Handshake names that no centralized authority can touch.

The Handshake resolvers (`103.196.38.38` / `103.196.38.39`) are the bridge. They resolve both traditional domains and Handshake TLDs. When this dVPN sets Handshake as the default DNS, every connection — every website, every API call, every lookup — flows through decentralized resolution before entering the decentralized tunnel.

**For the first time, the entire path is decentralized. Name resolution. Transport. Bandwidth. No single point of failure. No single point of control.**

---

## The Architecture of Freedom

Handshake dVPN combines three decentralized layers into one seamless experience:

### Layer 1: Decentralized Naming (Handshake)
Your device resolves domain names through Handshake DNS. Traditional domains work normally. Handshake TLDs (`.forever`, `.badass`, `.creator`) resolve too — names that no government can seize, no corporation can revoke, no registrar can suspend.

### Layer 2: Decentralized Transport (Sentinel)
Your traffic flows through WireGuard or V2Ray tunnels to peer-operated nodes. No central VPN server. No company logs. No jurisdiction. The Sentinel blockchain manages sessions, payments, and node discovery. 900+ nodes across 90+ countries, paid per gigabyte or per hour in P2P tokens.

### Layer 3: Decentralized Bandwidth (P2P Network)
Anyone can run a Sentinel node and earn P2P tokens for providing bandwidth. The network grows organically. More nodes means more speed, more resilience, more geographic coverage. The incentive structure ensures the network can never shrink to zero — as long as there is demand for privacy, there is profit in providing it.

---

## What We Believe

**DNS is infrastructure, not a service.** When Cloudflare goes down, half the internet goes dark. When a registrar complies with a takedown, a voice goes silent. DNS should be as unstoppable as mathematics. Handshake makes it so.

**Privacy is a stack, not a feature.** Encrypting your tunnel while leaking your DNS is like whispering secrets in a room full of microphones. Every layer must be private. Every layer must be decentralized. Every layer must be under the user's control.

**Pay-as-you-go is freedom.** No accounts. No subscriptions. No identity. No credit cards. Connect a wallet, pick a node, pay per gigabyte or per hour. The economic relationship is between you and a peer — no intermediary, no billing system, no record beyond the blockchain.

**The best interface is invisible.** Handshake DNS is set as default. The user doesn't configure it, doesn't think about it, doesn't even know it's happening. They just type a URL and it resolves — through a system no one controls. That's the goal: freedom so seamless it feels like the internet always worked this way.

---

## Why This Matters Now

In 2024 alone:
- Russia blocked 800,000+ domains via DNS filtering
- Iran used DNS poisoning to redirect protest coordination sites
- China's Great Firewall resolved blocked domains to `127.0.0.1`
- Multiple countries ordered registrars to seize opposition domains
- Cloudflare's 1.1.1.1 was temporarily blocked in several countries

Every one of these attacks targeted DNS. Every one would have been mitigated by Handshake resolution. Every one affected people whose VPNs were running but whose DNS was centralized.

**The attack surface is the naming layer. Handshake eliminates it.**

---

## The Technical Foundation

This application is built on the **Sentinel C# SDK** — a battle-tested SDK verified against 780+ live mainnet nodes with 22 production bugs discovered and fixed.

| Component | Technology |
|-----------|-----------|
| **DNS Resolution** | Handshake (103.196.38.38 / 103.196.38.39) |
| **Tunnel Protocol** | WireGuard (primary) / V2Ray (fallback) |
| **Blockchain** | Sentinel (Cosmos SDK chain, sentinelhub-2) |
| **Payment** | P2P token (udvpn), per-GB or per-hour |
| **Wallet** | BIP39/BIP44, secp256k1, Bech32 |
| **Handshake** | V3 ECDSA + X25519 key exchange |
| **Platform** | Windows (.NET 8.0 WPF) |

### Default DNS Configuration
When connected, the tunnel DNS is set to Handshake resolvers:
- **Primary:** `103.196.38.38` (HDNS)
- **Secondary:** `103.196.38.39` (HDNS)

This means:
- All `.com`, `.org`, `.net` domains resolve normally
- All Handshake TLDs resolve natively
- No DNS queries touch Google, Cloudflare, or any ISP resolver
- DNS-based censorship and filtering are bypassed at the protocol level

---

## No Plans. No Subscriptions. Just Nodes.

Unlike plan-based dVPN clients, Handshake dVPN gives you direct access to **every active node on the Sentinel network**. No middleman operator. No curated list. No markup.

- Browse all 900+ nodes sorted by country, protocol, and price
- Filter by WireGuard or V2Ray
- Filter by payment model: per-gigabyte or per-hour
- See real-time status: online/offline, peer count, bandwidth
- Pay the node's own price directly — no subscription, no lock-in
- Connect to any node, disconnect anytime, pay only for what you use

**Your wallet. Your choice. Your privacy.**

---

## Build Something Unstoppable

Handshake secures the names. Sentinel secures the pipes. Together they create something neither can alone: **an internet connection that no authority on Earth can fully surveil, censor, or shut down.**

This is not a product. It is a proof of concept that becomes a movement. Fork it. Build on it. Make it better. The protocols are open. The code is open. The future is open.

**The internet was meant to be free. We're finishing the job.**

---

*Handshake dVPN — Decentralized names. Decentralized transport. Decentralized freedom.*

*Powered by Sentinel. Secured by Handshake. Owned by no one.*
