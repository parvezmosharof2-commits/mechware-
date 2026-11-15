# Bangladesh locations: review note (A9)

Source of truth: `backend/src/Mechware.Infrastructure/Persistence/BangladeshLocationData.cs` (text data) parsed by `BangladeshLocationSeeder`. The migration `SeedBangladeshLocations` copies it into `bd_locations` (idempotent). The hierarchy was written from memory of the BBS/LGD lists, **not checked against a published file**. The owner should have a Bangladeshi reviewer check it before launch.

## Counts
8 divisions, 64 districts (Dhaka 13, Chattogram 11, Rajshahi 8, Khulna 10, Sylhet 4, Rangpur 8, Barishal 6, Mymensingh 4), 538 upazila rows. The 538 are 491 official upazilas (the national total is 492 to 495 depending on the source; Natore is the one I know is short) plus extras: the older industrial-zone rows (Tejgaon Industrial Area, Gulshan, Uttara, Savar EPZ, Tongi Industrial Zone, Siddhirganj Adamjee EPZ, Chattogram EPZ, Sitakunda zone, Pabna BSCIC, Ishwardi EPZ; kept with their ids) and city police stations (thanas) for Dhaka (23), Chattogram (10) and Khulna (4).

## Needs review by a Bangladeshi reviewer
| District | Rows | Why |
|---|---|---|
| Dhaka | 32 | 5 official upazilas are complete; the 23 DMP thanas are only the ones I am sure of, not the full list (about 50). |
| Chattogram | 27 | 15 official upazilas complete; 10 CCC thanas only. |
| Khulna | 13 | 9 official upazilas; 4 KCC thanas only. |
| Natore | 6 | Naldanga may be a 7th upazila; not included because I am not sure. |
| Rajshahi, Sylhet, Barishal, Mymensingh, Rangpur | official upazilas only | City-corporation thanas (Boalia, Rajpara, Motihar, ...) are NOT included, so a city address has to pick the nearest upazila. |
| Gazipur, Narayanganj, Pabna | industrial-zone rows plus official upazilas | "Pabna Sadar (BSCIC)" and "Pabna Sadar" both exist (same for Ishwardi); the old rows are kept because addresses may reference them. |

Everything else (all other districts) lists the full official upazila set as I know it. Still unverified: Bangla spellings (for example ঈ/ই, ী/ি variants, "কুমিল্লা" upazila names, Barishal "উজিরপুর"), recently created upazilas, and English spelling variants (Chattogram/Cumilla/Jashore follow current official forms). Nothing was invented: if a name was uncertain it was left out.

## Rules
- Append only: the position of a line is part of the id. Never reorder or delete lines; to fix a name edit it in place and ship a migration with an `UPDATE` (the INSERT is `ON CONFLICT DO NOTHING`, so it does not rewrite existing rows).
- Ids: division n = `11111111-0001-0000-0000-{n}`, district k of division n = `22222222-{n:D4}-0000-0000-{k}`, upazila u of district k = `33333333-{n:D4}-{k:D4}-0000-{u}` (counters, not hashes; unit tests check uniqueness and parents).
- `shipping_zone` text on rows is unused by the E8 zone model; new rows are `outside_dhaka` except Dhaka-area ones. Postal prefixes are null for new rows.
