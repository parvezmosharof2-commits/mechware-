namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// The Bangladesh administrative hierarchy as data (A9), parsed by <see cref="BangladeshLocationSeeder"/>. Append-only: the
/// position of a division, district or upazila in this text is part of its id, so never reorder or delete lines.
/// Line kinds: <c>D</c> division, <c>C</c> district (under the last D), <c>U</c> upazilas (under the last C, separated by ';').
/// Fields: <c>English|Bangla[|shippingZone[|postalCodePrefix]]</c>; the zone defaults to outside_dhaka, the postal prefix to none.
/// The first upazilas of Dhaka, Gazipur, Narayanganj, Chattogram and Pabna are the older industrial-zone rows, kept with their ids.
/// Source: written from the BBS/LGD hierarchy by memory, not checked against a published file; see docs/catalog/BD_LOCATIONS_REVIEW.md.
/// </summary>
internal static class BangladeshLocationData
{
    internal const string Text = """
        D Dhaka|ঢাকা|inside_dhaka|1000
        C Dhaka|ঢাকা|inside_dhaka|1200
        U Tejgaon Industrial Area|তেজগাঁও শিল্পাঞ্চল|inside_dhaka|1208; Gulshan|গুলশান|inside_dhaka|1212; Uttara|উত্তরা|inside_dhaka|1230; Savar EPZ|সাভার ইপিজেড|dhaka_suburb|1349
        U Dhamrai|ধামরাই|dhaka_suburb; Dohar|দোহার; Keraniganj|কেরানীগঞ্জ|dhaka_suburb; Nawabganj|নবাবগঞ্জ; Savar|সাভার|dhaka_suburb
        U Dhanmondi|ধানমন্ডি|inside_dhaka; Mohammadpur|মোহাম্মদপুর|inside_dhaka; Mirpur|মিরপুর|inside_dhaka; Motijheel|মতিঝিল|inside_dhaka; Banani|বনানী|inside_dhaka; Badda|বাড্ডা|inside_dhaka; Khilgaon|খিলগাঁও|inside_dhaka; Demra|ডেমরা|inside_dhaka; Jatrabari|যাত্রাবাড়ী|inside_dhaka; Lalbagh|লালবাগ|inside_dhaka; Kotwali|কোতোয়ালী|inside_dhaka; Sutrapur|সূত্রাপুর|inside_dhaka; Pallabi|পল্লবী|inside_dhaka; Kafrul|কাফরুল|inside_dhaka; Ramna|রমনা|inside_dhaka; Shahbag|শাহবাগ|inside_dhaka; Hazaribagh|হাজারীবাগ|inside_dhaka; Sabujbagh|সবুজবাগ|inside_dhaka; Rampura|রামপুরা|inside_dhaka; Bangshal|বংশাল|inside_dhaka; Cantonment|ক্যান্টনমেন্ট|inside_dhaka; Khilkhet|খিলক্ষেত|inside_dhaka; Vatara|ভাটারা|inside_dhaka
        C Gazipur|গাজীপুর|dhaka_suburb|1700
        U Tongi Industrial Zone|টঙ্গী শিল্পাঞ্চল|dhaka_suburb|1710; Gazipur Sadar|গাজীপুর সদর|dhaka_suburb|1700
        U Kaliakair|কালিয়াকৈর|dhaka_suburb; Kaliganj|কালীগঞ্জ; Kapasia|কাপাসিয়া; Sreepur|শ্রীপুর
        C Narayanganj|নারায়ণগঞ্জ|dhaka_suburb|1400
        U Siddhirganj Adamjee EPZ|সিদ্ধিরগঞ্জ আদমজী ইপিজেড|dhaka_suburb|1431
        U Araihazar|আড়াইহাজার; Bandar|বন্দর|dhaka_suburb; Narayanganj Sadar|নারায়ণগঞ্জ সদর|dhaka_suburb; Rupganj|রূপগঞ্জ|dhaka_suburb; Sonargaon|সোনারগাঁও
        C Faridpur|ফরিদপুর
        U Alfadanga|আলফাডাঙ্গা; Bhanga|ভাঙ্গা; Boalmari|বোয়ালমারী; Charbhadrasan|চরভদ্রাসন; Faridpur Sadar|ফরিদপুর সদর; Madhukhali|মধুখালী; Nagarkanda|নগরকান্দা; Sadarpur|সদরপুর; Saltha|সালথা
        C Gopalganj|গোপালগঞ্জ
        U Gopalganj Sadar|গোপালগঞ্জ সদর; Kashiani|কাশিয়ানী; Kotalipara|কোটালীপাড়া; Muksudpur|মুকসুদপুর; Tungipara|টুঙ্গিপাড়া
        C Kishoreganj|কিশোরগঞ্জ
        U Austagram|অষ্টগ্রাম; Bajitpur|বাজিতপুর; Bhairab|ভৈরব; Hossainpur|হোসেনপুর; Itna|ইটনা; Karimganj|করিমগঞ্জ; Katiadi|কটিয়াদী; Kishoreganj Sadar|কিশোরগঞ্জ সদর; Kuliarchar|কুলিয়ারচর; Mithamain|মিঠামইন; Nikli|নিকলী; Pakundia|পাকুন্দিয়া; Tarail|তাড়াইল
        C Madaripur|মাদারীপুর
        U Madaripur Sadar|মাদারীপুর সদর; Kalkini|কালকিনি; Rajoir|রাজৈর; Shibchar|শিবচর
        C Manikganj|মানিকগঞ্জ
        U Daulatpur|দৌলতপুর; Ghior|ঘিওর; Harirampur|হরিরামপুর; Manikganj Sadar|মানিকগঞ্জ সদর; Saturia|সাটুরিয়া; Shivalaya|শিবালয়; Singair|সিংগাইর
        C Munshiganj|মুন্সীগঞ্জ
        U Gazaria|গজারিয়া; Lohajang|লৌহজং; Munshiganj Sadar|মুন্সীগঞ্জ সদর; Sirajdikhan|সিরাজদিখান; Sreenagar|শ্রীনগর; Tongibari|টংগীবাড়ী
        C Narsingdi|নরসিংদী
        U Belabo|বেলাবো; Monohardi|মনোহরদী; Narsingdi Sadar|নরসিংদী সদর; Palash|পলাশ; Raipura|রায়পুরা; Shibpur|শিবপুর
        C Rajbari|রাজবাড়ী
        U Baliakandi|বালিয়াকান্দি; Goalanda|গোয়ালন্দ; Kalukhali|কালুখালী; Pangsha|পাংশা; Rajbari Sadar|রাজবাড়ী সদর
        C Shariatpur|শরীয়তপুর
        U Bhedarganj|ভেদরগঞ্জ; Damudya|ডামুড্যা; Gosairhat|গোসাইরহাট; Naria|নড়িয়া; Shariatpur Sadar|শরীয়তপুর সদর; Zajira|জাজিরা
        C Tangail|টাঙ্গাইল
        U Basail|বাসাইল; Bhuapur|ভূঞাপুর; Delduar|দেলদুয়ার; Dhanbari|ধনবাড়ী; Ghatail|ঘাটাইল; Gopalpur|গোপালপুর; Kalihati|কালিহাতী; Madhupur|মধুপুর; Mirzapur|মির্জাপুর; Nagarpur|নাগরপুর; Sakhipur|সখিপুর; Tangail Sadar|টাঙ্গাইল সদর
        D Chattogram|চট্টগ্রাম|outside_dhaka|4000
        C Chattogram|চট্টগ্রাম|outside_dhaka|4000
        U Chattogram EPZ (Halishahar)|চট্টগ্রাম ইপিজেড (হালিশহর)|outside_dhaka|4223; Sitakunda Shipbreaking & Steel Zone|সীতাকুণ্ড শিল্পাঞ্চল|outside_dhaka|4310
        U Anwara|আনোয়ারা; Banshkhali|বাঁশখালী; Boalkhali|বোয়ালখালী; Chandanaish|চন্দনাইশ; Fatikchhari|ফটিকছড়ি; Hathazari|হাটহাজারী; Lohagara|লোহাগাড়া; Mirsharai|মিরসরাই; Patiya|পটিয়া; Rangunia|রাঙ্গুনিয়া; Raozan|রাউজান; Sandwip|সন্দ্বীপ; Satkania|সাতকানিয়া; Sitakunda|সীতাকুণ্ড; Karnaphuli|কর্ণফুলী
        U Kotwali|কোতোয়ালী; Pahartali|পাহাড়তলী; Panchlaish|পাঁচলাইশ; Khulshi|খুলশী; Double Mooring|ডবলমুরিং; Chandgaon|চান্দগাঁও; Bayazid|বায়েজিদ; Halishahar|হালিশহর; Patenga|পতেঙ্গা; Akbar Shah|আকবর শাহ
        C Bandarban|বান্দরবান
        U Alikadam|আলীকদম; Bandarban Sadar|বান্দরবান সদর; Lama|লামা; Naikhongchhari|নাইক্ষ্যংছড়ি; Rowangchhari|রোয়াংছড়ি; Ruma|রুমা; Thanchi|থানচি
        C Brahmanbaria|ব্রাহ্মণবাড়িয়া
        U Akhaura|আখাউড়া; Ashuganj|আশুগঞ্জ; Bancharampur|বাঞ্ছারামপুর; Bijoynagar|বিজয়নগর; Brahmanbaria Sadar|ব্রাহ্মণবাড়িয়া সদর; Kasba|কসবা; Nabinagar|নবীনগর; Nasirnagar|নাসিরনগর; Sarail|সরাইল
        C Chandpur|চাঁদপুর
        U Faridganj|ফরিদগঞ্জ; Haimchar|হাইমচর; Haziganj|হাজীগঞ্জ; Kachua|কচুয়া; Matlab Dakshin|মতলব দক্ষিণ; Matlab Uttar|মতলব উত্তর; Shahrasti|শাহরাস্তি; Chandpur Sadar|চাঁদপুর সদর
        C Cox's Bazar|কক্সবাজার
        U Chakaria|চকরিয়া; Cox's Bazar Sadar|কক্সবাজার সদর; Kutubdia|কুতুবদিয়া; Maheshkhali|মহেশখালী; Pekua|পেকুয়া; Ramu|রামু; Teknaf|টেকনাফ; Ukhia|উখিয়া
        C Cumilla|কুমিল্লা
        U Barura|বরুড়া; Brahmanpara|ব্রাহ্মণপাড়া; Burichang|বুড়িচং; Chandina|চান্দিনা; Chauddagram|চৌদ্দগ্রাম; Daudkandi|দাউদকান্দি; Debidwar|দেবিদ্বার; Homna|হোমনা; Laksam|লাকসাম; Lalmai|লালমাই; Meghna|মেঘনা; Monoharganj|মনোহরগঞ্জ; Muradnagar|মুরাদনগর; Nangalkot|নাঙ্গলকোট; Cumilla Adarsha Sadar|কুমিল্লা আদর্শ সদর; Cumilla Sadar Dakshin|কুমিল্লা সদর দক্ষিণ; Titas|তিতাস
        C Feni|ফেনী
        U Chhagalnaiya|ছাগলনাইয়া; Daganbhuiyan|দাগনভূঞা; Feni Sadar|ফেনী সদর; Fulgazi|ফুলগাজী; Parshuram|পরশুরাম; Sonagazi|সোনাগাজী
        C Khagrachhari|খাগড়াছড়ি
        U Dighinala|দীঘিনালা; Guimara|গুইমারা; Khagrachhari Sadar|খাগড়াছড়ি সদর; Lakshmichhari|লক্ষ্মীছড়ি; Mahalchhari|মহালছড়ি; Manikchhari|মানিকছড়ি; Matiranga|মাটিরাঙ্গা; Panchhari|পানছড়ি; Ramgarh|রামগড়
        C Lakshmipur|লক্ষ্মীপুর
        U Kamalnagar|কমলনগর; Lakshmipur Sadar|লক্ষ্মীপুর সদর; Raipur|রায়পুর; Ramganj|রামগঞ্জ; Ramgati|রামগতি
        C Noakhali|নোয়াখালী
        U Begumganj|বেগমগঞ্জ; Chatkhil|চাটখিল; Companiganj|কোম্পানীগঞ্জ; Hatiya|হাতিয়া; Kabirhat|কবিরহাট; Noakhali Sadar|নোয়াখালী সদর; Senbagh|সেনবাগ; Sonaimuri|সোনাইমুড়ী; Subarnachar|সুবর্ণচর
        C Rangamati|রাঙ্গামাটি
        U Baghaichhari|বাঘাইছড়ি; Barkal|বরকল; Belaichhari|বিলাইছড়ি; Juraichhari|জুরাছড়ি; Kaptai|কাপ্তাই; Kawkhali|কাউখালী; Langadu|লংগদু; Naniarchar|নানিয়ারচর; Rajasthali|রাজস্থলী; Rangamati Sadar|রাঙ্গামাটি সদর
        D Rajshahi|রাজশাহী|outside_dhaka|6000
        C Pabna|পাবনা|outside_dhaka|6600
        U Pabna Sadar (BSCIC)|পাবনা সদর (বিসিক)|outside_dhaka|6600; Ishwardi EPZ|ঈশ্বরদী ইপিজেড|outside_dhaka|6620
        U Atgharia|আটঘরিয়া; Bera|বেড়া; Bhangura|ভাঙ্গুড়া; Chatmohar|চাটমোহর; Faridpur|ফরিদপুর; Ishwardi|ঈশ্বরদী; Pabna Sadar|পাবনা সদর; Santhia|সাঁথিয়া; Sujanagar|সুজানগর
        C Rajshahi|রাজশাহী|outside_dhaka|6000
        U Bagha|বাঘা; Bagmara|বাগমারা; Charghat|চারঘাট; Durgapur|দুর্গাপুর; Godagari|গোদাগাড়ী; Mohanpur|মোহনপুর; Paba|পবা; Puthia|পুঠিয়া; Tanore|তানোর
        C Bogura|বগুড়া
        U Adamdighi|আদমদীঘি; Bogura Sadar|বগুড়া সদর; Dhunat|ধুনট; Dhupchanchia|দুপচাঁচিয়া; Gabtali|গাবতলী; Kahaloo|কাহালু; Nandigram|নন্দীগ্রাম; Sariakandi|সারিয়াকান্দি; Shajahanpur|শাজাহানপুর; Sherpur|শেরপুর; Shibganj|শিবগঞ্জ; Sonatala|সোনাতলা
        C Joypurhat|জয়পুরহাট
        U Akkelpur|আক্কেলপুর; Joypurhat Sadar|জয়পুরহাট সদর; Kalai|কালাই; Khetlal|ক্ষেতলাল; Panchbibi|পাঁচবিবি
        C Naogaon|নওগাঁ
        U Atrai|আত্রাই; Badalgachhi|বদলগাছী; Dhamoirhat|ধামইরহাট; Manda|মান্দা; Mohadevpur|মহাদেবপুর; Naogaon Sadar|নওগাঁ সদর; Niamatpur|নিয়ামতপুর; Patnitala|পত্নীতলা; Porsha|পোরশা; Raninagar|রানীনগর; Sapahar|সাপাহার
        C Natore|নাটোর
        U Bagatipara|বাগাতিপাড়া; Baraigram|বড়াইগ্রাম; Gurudaspur|গুরুদাসপুর; Lalpur|লালপুর; Natore Sadar|নাটোর সদর; Singra|সিংড়া
        C Chapainawabganj|চাঁপাইনবাবগঞ্জ
        U Bholahat|ভোলাহাট; Gomastapur|গোমস্তাপুর; Nachole|নাচোল; Nawabganj Sadar|নবাবগঞ্জ সদর; Shibganj|শিবগঞ্জ
        C Sirajganj|সিরাজগঞ্জ
        U Belkuchi|বেলকুচি; Chauhali|চৌহালী; Kamarkhanda|কামারখন্দ; Kazipur|কাজীপুর; Raiganj|রায়গঞ্জ; Shahjadpur|শাহজাদপুর; Sirajganj Sadar|সিরাজগঞ্জ সদর; Tarash|তাড়াশ; Ullahpara|উল্লাপাড়া
        D Khulna|খুলনা|outside_dhaka|9000
        C Khulna|খুলনা|outside_dhaka|9000
        U Batiaghata|বটিয়াঘাটা; Dacope|দাকোপ; Dighalia|দিঘলিয়া; Dumuria|ডুমুরিয়া; Koyra|কয়রা; Paikgachha|পাইকগাছা; Phultala|ফুলতলা; Rupsha|রূপসা; Terokhada|তেরখাদা
        U Khulna Sadar|খুলনা সদর; Sonadanga|সোনাডাঙ্গা; Khalishpur|খালিশপুর; Daulatpur|দৌলতপুর
        C Bagerhat|বাগেরহাট
        U Bagerhat Sadar|বাগেরহাট সদর; Chitalmari|চিতলমারী; Fakirhat|ফকিরহাট; Kachua|কচুয়া; Mollahat|মোল্লাহাট; Mongla|মোংলা; Morrelganj|মোড়েলগঞ্জ; Rampal|রামপাল; Sarankhola|শরণখোলা
        C Chuadanga|চুয়াডাঙ্গা
        U Alamdanga|আলমডাঙ্গা; Chuadanga Sadar|চুয়াডাঙ্গা সদর; Damurhuda|দামুড়হুদা; Jibannagar|জীবননগর
        C Jashore|যশোর
        U Abhaynagar|অভয়নগর; Bagherpara|বাঘারপাড়া; Chaugachha|চৌগাছা; Jashore Sadar|যশোর সদর; Jhikargachha|ঝিকরগাছা; Keshabpur|কেশবপুর; Manirampur|মনিরামপুর; Sharsha|শার্শা
        C Jhenaidah|ঝিনাইদহ
        U Harinakunda|হরিণাকুণ্ডু; Jhenaidah Sadar|ঝিনাইদহ সদর; Kaliganj|কালীগঞ্জ; Kotchandpur|কোটচাঁদপুর; Maheshpur|মহেশপুর; Shailkupa|শৈলকুপা
        C Kushtia|কুষ্টিয়া
        U Bheramara|ভেড়ামারা; Daulatpur|দৌলতপুর; Khoksa|খোকসা; Kumarkhali|কুমারখালী; Kushtia Sadar|কুষ্টিয়া সদর; Mirpur|মিরপুর
        C Magura|মাগুরা
        U Magura Sadar|মাগুরা সদর; Mohammadpur|মহম্মদপুর; Shalikha|শালিখা; Sreepur|শ্রীপুর
        C Meherpur|মেহেরপুর
        U Gangni|গাংনী; Meherpur Sadar|মেহেরপুর সদর; Mujibnagar|মুজিবনগর
        C Narail|নড়াইল
        U Kalia|কালিয়া; Lohagara|লোহাগড়া; Narail Sadar|নড়াইল সদর
        C Satkhira|সাতক্ষীরা
        U Assasuni|আশাশুনি; Debhata|দেবহাটা; Kalaroa|কলারোয়া; Kaliganj|কালিগঞ্জ; Satkhira Sadar|সাতক্ষীরা সদর; Shyamnagar|শ্যামনগর; Tala|তালা
        D Sylhet|সিলেট|outside_dhaka|3100
        C Sylhet|সিলেট|outside_dhaka|3100
        U Balaganj|বালাগঞ্জ; Beanibazar|বিয়ানীবাজার; Bishwanath|বিশ্বনাথ; Companiganj|কোম্পানীগঞ্জ; Dakshin Surma|দক্ষিণ সুরমা; Fenchuganj|ফেঞ্চুগঞ্জ; Golapganj|গোলাপগঞ্জ; Gowainghat|গোয়াইনঘাট; Jaintiapur|জৈন্তাপুর; Kanaighat|কানাইঘাট; Sylhet Sadar|সিলেট সদর; Zakiganj|জকিগঞ্জ; Osmani Nagar|ওসমানী নগর
        C Habiganj|হবিগঞ্জ
        U Ajmiriganj|আজমিরীগঞ্জ; Bahubal|বাহুবল; Baniachong|বানিয়াচং; Chunarughat|চুনারুঘাট; Habiganj Sadar|হবিগঞ্জ সদর; Lakhai|লাখাই; Madhabpur|মাধবপুর; Nabiganj|নবীগঞ্জ
        C Moulvibazar|মৌলভীবাজার
        U Barlekha|বড়লেখা; Juri|জুড়ী; Kamalganj|কমলগঞ্জ; Kulaura|কুলাউড়া; Moulvibazar Sadar|মৌলভীবাজার সদর; Rajnagar|রাজনগর; Sreemangal|শ্রীমঙ্গল
        C Sunamganj|সুনামগঞ্জ
        U Bishwamvarpur|বিশ্বম্ভরপুর; Chhatak|ছাতক; Dakshin Sunamganj|দক্ষিণ সুনামগঞ্জ; Derai|দিরাই; Dharamapasha|ধর্মপাশা; Dowarabazar|দোয়ারাবাজার; Jagannathpur|জগন্নাথপুর; Jamalganj|জামালগঞ্জ; Sullah|শাল্লা; Sunamganj Sadar|সুনামগঞ্জ সদর; Tahirpur|তাহিরপুর; Madhyanagar|মধ্যনগর
        D Rangpur|রংপুর|outside_dhaka|5400
        C Rangpur|রংপুর
        U Badarganj|বদরগঞ্জ; Gangachara|গঙ্গাচড়া; Kaunia|কাউনিয়া; Mithapukur|মিঠাপুকুর; Pirgachha|পীরগাছা; Pirganj|পীরগঞ্জ; Rangpur Sadar|রংপুর সদর; Taraganj|তারাগঞ্জ
        C Dinajpur|দিনাজপুর
        U Birampur|বিরামপুর; Birganj|বীরগঞ্জ; Biral|বিরল; Bochaganj|বোচাগঞ্জ; Chirirbandar|চিরিরবন্দর; Phulbari|ফুলবাড়ী; Ghoraghat|ঘোড়াঘাট; Hakimpur|হাকিমপুর; Kaharole|কাহারোল; Khansama|খানসামা; Dinajpur Sadar|দিনাজপুর সদর; Nawabganj|নবাবগঞ্জ; Parbatipur|পার্বতীপুর
        C Gaibandha|গাইবান্ধা
        U Fulchhari|ফুলছড়ি; Gaibandha Sadar|গাইবান্ধা সদর; Gobindaganj|গোবিন্দগঞ্জ; Palashbari|পলাশবাড়ী; Sadullapur|সাদুল্লাপুর; Saghata|সাঘাটা; Sundarganj|সুন্দরগঞ্জ
        C Kurigram|কুড়িগ্রাম
        U Bhurungamari|ভূরুঙ্গামারী; Char Rajibpur|চর রাজিবপুর; Chilmari|চিলমারী; Phulbari|ফুলবাড়ী; Kurigram Sadar|কুড়িগ্রাম সদর; Nageshwari|নাগেশ্বরী; Rajarhat|রাজারহাট; Raumari|রৌমারী; Ulipur|উলিপুর
        C Lalmonirhat|লালমনিরহাট
        U Aditmari|আদিতমারী; Hatibandha|হাতীবান্ধা; Kaliganj|কালীগঞ্জ; Lalmonirhat Sadar|লালমনিরহাট সদর; Patgram|পাটগ্রাম
        C Nilphamari|নীলফামারী
        U Dimla|ডিমলা; Domar|ডোমার; Jaldhaka|জলঢাকা; Kishoreganj|কিশোরগঞ্জ; Nilphamari Sadar|নীলফামারী সদর; Saidpur|সৈয়দপুর
        C Panchagarh|পঞ্চগড়
        U Atwari|আটোয়ারী; Boda|বোদা; Debiganj|দেবীগঞ্জ; Panchagarh Sadar|পঞ্চগড় সদর; Tetulia|তেঁতুলিয়া
        C Thakurgaon|ঠাকুরগাঁও
        U Baliadangi|বালিয়াডাঙ্গী; Haripur|হরিপুর; Pirganj|পীরগঞ্জ; Ranisankail|রাণীশংকৈল; Thakurgaon Sadar|ঠাকুরগাঁও সদর
        D Barishal|বরিশাল|outside_dhaka|8200
        C Barishal|বরিশাল
        U Agailjhara|আগৈলঝাড়া; Babuganj|বাবুগঞ্জ; Bakerganj|বাকেরগঞ্জ; Banaripara|বানারীপাড়া; Gaurnadi|গৌরনদী; Hizla|হিজলা; Barishal Sadar|বরিশাল সদর; Mehendiganj|মেহেন্দিগঞ্জ; Muladi|মুলাদী; Wazirpur|উজিরপুর
        C Barguna|বরগুনা
        U Amtali|আমতলী; Bamna|বামনা; Barguna Sadar|বরগুনা সদর; Betagi|বেতাগী; Patharghata|পাথরঘাটা; Taltali|তালতলী
        C Bhola|ভোলা
        U Bhola Sadar|ভোলা সদর; Burhanuddin|বুরহানউদ্দিন; Char Fasson|চরফ্যাশন; Daulatkhan|দৌলতখান; Lalmohan|লালমোহন; Manpura|মনপুরা; Tazumuddin|তজুমদ্দিন
        C Jhalokati|ঝালকাঠি
        U Jhalokati Sadar|ঝালকাঠি সদর; Kathalia|কাঁঠালিয়া; Nalchity|নলছিটি; Rajapur|রাজাপুর
        C Patuakhali|পটুয়াখালী
        U Bauphal|বাউফল; Dashmina|দশমিনা; Dumki|দুমকি; Galachipa|গলাচিপা; Kalapara|কলাপাড়া; Mirzaganj|মির্জাগঞ্জ; Patuakhali Sadar|পটুয়াখালী সদর; Rangabali|রাঙ্গাবালী
        C Pirojpur|পিরোজপুর
        U Bhandaria|ভান্ডারিয়া; Kawkhali|কাউখালী; Mathbaria|মঠবাড়িয়া; Nazirpur|নাজিরপুর; Nesarabad|নেছারাবাদ; Pirojpur Sadar|পিরোজপুর সদর; Zianagar|জিয়ানগর
        D Mymensingh|ময়মনসিংহ|outside_dhaka|2200
        C Mymensingh|ময়মনসিংহ
        U Bhaluka|ভালুকা; Trishal|ত্রিশাল; Haluaghat|হালুয়াঘাট; Muktagachha|মুক্তাগাছা; Dhobaura|ধোবাউড়া; Fulbaria|ফুলবাড়িয়া; Gaffargaon|গফরগাঁও; Gauripur|গৌরীপুর; Ishwarganj|ঈশ্বরগঞ্জ; Mymensingh Sadar|ময়মনসিংহ সদর; Nandail|নান্দাইল; Phulpur|ফুলপুর; Tarakanda|তারাকান্দা
        C Jamalpur|জামালপুর
        U Baksiganj|বকশীগঞ্জ; Dewanganj|দেওয়ানগঞ্জ; Islampur|ইসলামপুর; Jamalpur Sadar|জামালপুর সদর; Madarganj|মাদারগঞ্জ; Melandaha|মেলান্দহ; Sarishabari|সরিষাবাড়ী
        C Netrokona|নেত্রকোণা
        U Atpara|আটপাড়া; Barhatta|বারহাট্টা; Durgapur|দুর্গাপুর; Khaliajuri|খালিয়াজুরী; Kalmakanda|কলমাকান্দা; Kendua|কেন্দুয়া; Madan|মদন; Mohanganj|মোহনগঞ্জ; Netrokona Sadar|নেত্রকোণা সদর; Purbadhala|পূর্বধলা
        C Sherpur|শেরপুর
        U Jhenaigati|ঝিনাইগাতী; Nakla|নকলা; Nalitabari|নালিতাবাড়ী; Sherpur Sadar|শেরপুর সদর; Sreebardi|শ্রীবরদী
        """;
}
