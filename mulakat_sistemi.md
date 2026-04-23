# Mulakat Sistemi

## Amac

Bu belge, projedeki mulakat sisteminin:

1. su anda nasil calismasi gerektigini,
2. kod tabaninda hangi parcalardan olustugunu,
3. verinin sistem icinde nasil aktigini,
4. bugun neden istenen kaliteye ulasamadigini,
5. hedefledigimiz daha dogru ve daha dayanikli yapinin ne oldugunu

tek bir yerde, uygulamayi gelistiren veya debug eden kisiye anlatmak icin hazirlanmistir.

Bu dokuman hem teknik rehber, hem de sistemin "ideal calisma sekli" icin referans metindir.

---

## 1. Sistemin Ana Fikri

Bu proje bir yapay zeka destekli mulakat koçu sistemidir.

Kullanicinin bir mulakat oturumu boyunca:

- sesi,
- konusma metni,
- goruntu tabanli davranis metrikleri,
- soru-cevap akisi,
- ve oturum sonu degerlendirme bilgileri

toplanir, saklanir ve sonunda rapora donusturulur.

Sistem sadece "sesi yaziya ceviren" bir uygulama degildir. Asil amac:

- canli mulakat deneyimi sunmak,
- davranissal sinyalleri toplamak,
- transcript uretmek,
- transcript ve olaylari backend'e islemek,
- oturum sonunda puanlama ve geri bildirim cikarmak,
- gerekirse LLM destekli coaching uretmektir.

---

## 2. Sistemin Ana Bilesenleri

Sistemde temel olarak 4 ana katman vardir:

### 2.1 Frontend

Konum:

- `src/frontend/src`

Ana gorevleri:

- oturum baslatmak,
- kamera ve mikrofon erisimi almak,
- canli transcript panelini gostermek,
- goruntu tabanli metrikleri hesaplamak,
- bu metrikleri backend'e batch olarak gondermek,
- speech-service ile websocket uzerinden canli ASR kurmak,
- oturum sonunda transcript ve rapor ekranina gecmek.

En kritik sayfa:

- `src/frontend/src/pages/InterviewSession.tsx`

Bu dosya sistemin canli mulakat tarafindaki merkezi orkestrasyon noktasidir.

### 2.2 Backend API

Konum:

- `src/backend/InterviewCoach.Api`

Ana gorevleri:

- session olusturmak,
- soru listesini getirmek veya seed etmek,
- metric event batch'lerini almak,
- transcript batch'lerini almak,
- transcript segmentlerini merge etmek,
- oturumu finalize etmek,
- rapor ve evidence summary uretmek,
- gerekirse LLM coaching cagrisini yonetmektir.

Temel endpoint aileleri:

- `POST /api/sessions`
- `POST /api/sessions/{sessionId}/events/batch`
- `POST /api/sessions/{sessionId}/transcript/batch`
- `POST /api/sessions/{sessionId}/finalize`
- `GET /api/reports/{sessionId}`
- `GET /api/sessions/{sessionId}/evidence-summary`

### 2.3 Speech Service

Konum:

- `services/speech-service`

Ana gorevleri:

- canli ses parcalarini websocket uzerinden almak,
- bunlari uygun pencereleme ve VAD mantigi ile decode etmek,
- partial ve final transcript mesajlari uretmek,
- transcript kalitesini olabildigince stabil tutmak,
- readiness ve health bilgisi sunmaktir.

Temel endpoint:

- `ws://.../ws/transcribe?session_id=<id>&lang=tr|en`

Onemli readiness endpoint'i:

- `GET /health/ready`

### 2.4 Veri Katmani

Temel saklanan varliklar:

- `Sessions`
- `Questions`
- `MetricEvents`
- `TranscriptSegments`
- `ScoreCards`
- `FeedbackItems`

Bu katman sistemin "ham olaylardan rapora giden" kalici hafizasidir.

---

## 3. Sistem Normalde Nasil Islemeli

Bu bolum "happy path" yani her sey dogru calistiginda bekledigimiz akisi anlatir.

### 3.1 Oturum olusturma

Kullanici ana sayfadan:

- rol secer,
- dil secer,
- yeni bir session olusturur.

Frontend `POST /api/sessions` ile yeni oturumu acar. Backend session kaydini veritabanina yazar.

Sonra:

- session bilgisi cekilir,
- soru listesi yuklenir,
- gerekiyorsa sorular seed edilir.

### 3.2 Interview ekraninin hazirlanmasi

`InterviewSession.tsx` yuklenince sayfa su seyleri hazirlar:

- session verisini alir,
- soru listesini getirir,
- MediaPipe vision tarafini initialize etmeye calisir,
- canli session transport mekanizmasini kurar.

Buradaki temel fikir sudur:

- vision tarafi real-time davranis analizi icin,
- audio tarafi transcript icin,
- backend event batch tarafi ise kalici olay kaydi icindir.

### 3.3 Kayit baslatma

Kullanici kaydi baslattiginda frontend:

1. mikrofon ve kameraya erisir,
2. video stream'i ekrana verir,
3. sesi `MediaRecorder` ile parcalayip saklar,
4. ses analizoru ve diger yardimci bileşenleri baslatir,
5. canli ASR baglantisini kurmaya calisir,
6. event transport katmanini calistirir.

Bu adimda olmasi gereken ideal davranis:

- vision hazirsa goruntu metrikleri uretilir,
- vision hazir degilse sistem yine audio transcript ile devam eder,
- transcript paneli baglanti durumunu kullaniciya acikca gosterir.

### 3.4 Canli transcript akisi

Frontend speech-service ile websocket baglantisi kurar.

Akis su sekildedir:

1. frontend mikrofon stream'inden audio chunk'lar uretir,
2. bu chunk'lari websocket ile speech-service'e yollar,
3. speech-service stream'i biriktirir,
4. VAD ile "bu kisimda anlamli konusma var mi" diye bakar,
5. decode islemini tetikler,
6. once `partial`, sonra uygun zamanda `final` mesajlari doner,
7. frontend partial metni gecici olarak gosterir,
8. final segmentleri ise kalici satirlara cevirir,
9. final segmentler backend'e `transcript/batch` olarak gonderilir.

Buradaki en kritik prensip:

- partial kullaniciya "sistem duyuyor" hissi verir,
- final ise kalici transcript olarak saklanir.

### 3.5 Davranis metrikleri akisi

Vision tarafinda MediaPipe ve metrics computer kullanilarak:

- eye contact,
- posture,
- fidget,
- head stability veya head jitter,
- eye openness,
- emotion dagilimi

gibi sinyaller uretilir.

Bu sinyaller dogrudan veritabanina tek tek degil, batch mantigi ile gonderilir.

`SessionTransport` sinifinin gorevi:

- event kuyrugu tutmak,
- belirli araliklarla backend'e batch gondermek,
- gecici hatalarda retry yapmak,
- kuyruk fazla buyurse kontrollu sekilde eski eventleri dusurmektir.

Bu sayede canli deneyim takilsa bile telemetry tamamen dagilmaz.

### 3.6 Soru gecisi

Kullanici bir soruyu bitirdiginde:

- canli kayit durdurulur,
- audio blob elde tutulur,
- gerekiyorsa offline transcription da denenir,
- ara sorularda bu islem UX'i bloklamadan arka planda surer,
- son soruda ise finalize oncesi en guncel transcript'in sisteme ulasmasi hedeflenir.

### 3.7 Finalize ve rapor

Oturum bittiginde frontend:

- `POST /api/sessions/{sessionId}/finalize`

cagrisi yapar.

Backend bu noktada:

1. session'i yukler,
2. transcript segmentlerini alir,
3. metric eventleri alir,
4. score card hesaplar,
5. feedback item'lari uretir,
6. session durumunu `Completed` yapar,
7. rapor ekraninda gosterilecek yapilari hazirlar.

Sonrasinda:

- `GET /api/reports/{sessionId}`

ile rapor okunur.

Istenirse:

- evidence summary,
- export,
- LLM coaching

gibi ikincil urunler de alinabilir.

---

## 4. Mevcut Kodda Roller ve Sorumluluklar

### 4.1 `InterviewSession.tsx`

Bu dosya mevcut canli sistemin kalbidir.

Baslica sorumluluklari:

- session yukleme,
- soru yonetimi,
- recording start/stop,
- MediaPipe entegrasyonu,
- websocket ASR baslatma,
- final transcript batch ingestion,
- live analysis tetikleme,
- finalize'a gecis.

Bu dosya teknik olarak bir "page" olsa da mantiksal olarak bir orkestrator gorevi gorur.

### 4.2 `SessionTransport.ts`

Bu sinif frontend tarafindaki event tasima altyapisidir.

Sorumlulugu:

- metric eventleri queue etmek,
- belirli araliklarla gondermek,
- hata halinde retry yapmak,
- kuyruk tasmasini kontrol etmek.

Yani sistemin real-time telemetri omurgasidir.

### 4.3 `ApiService.ts`

Backend ile HTTP haberlesmesinin merkezidir.

Bu servis:

- session olusturma,
- soru cekme,
- transcript batch gonderme,
- finalize,
- rapor,
- coaching

gibi cagrilari tek elde toplar.

### 4.4 `services/speech-service/app/main.py`

Bu dosya speech-service'in merkezidir.

Sorumluluklari:

- modeli startup'ta yuklemek,
- readiness durumunu expose etmek,
- websocket baglantilarini kabul etmek,
- session bazli stream state tutmak,
- audio chunk akisini islemek,
- VAD ile ses/konusma ayrimi yapmak,
- decode kuyruğu yonetmek,
- partial ve final ciktilari client'a dondurmek.

### 4.5 `faster_whisper_backend.py`

Bu dosya speech model ile dogrudan iletisim katmanidir.

Sorumlulugu:

- modeli yuklemek,
- sesi whisper/faster-whisper'a vermek,
- decode parametrelerini uygulamak,
- kalitesiz veya supheli segmentleri filtrelemektir.

---

## 5. Su Anda Sistemde Calisan Gercek Veri Akisi

Kod tabanina gore bugunku gercek akis su sekildedir:

### 5.1 Frontend tarafi

- Session sayfasi acilir.
- Session ve sorular yuklenir.
- MediaPipe hazirlanmaya calisilir.
- Kullanici kaydi baslatir.
- `getUserMedia` ile kamera ve mikrofon alinir.
- Event transport baslatilir.
- `startStreamingAsr()` speech-service readiness kontrolu yapar.
- Speech hazirsa websocket baglanir.
- Final transcript segmentleri geldikce backend'e `transcript/batch` olarak yazilir.

### 5.2 Speech-service tarafi

- Servis startup'ta modeli yuklemeye calisir.
- `/health/ready` modeli ve kapasiteyi kontrol eder.
- `/ws/transcribe` baglantiyi kabul eder.
- Session configure edilir.
- Audio chunk'lar queue'ya alinir.
- Processor loop bunlari stream window'ya yazar.
- VAD anlamli konusma algilarsa decode kuyruguna job ekler.
- Transcriber loop modeli cagirir.
- Sonuc `partial` ve `final` olarak gonderilir.

### 5.3 Backend tarafi

- Vision ve diger eventler `events/batch` ile yazilir.
- Final transcript `transcript/batch` ile merge edilerek yazilir.
- Finalize cagrisi bu iki veri kaynagini kullanarak score ve feedback uretir.

---

## 6. Bu Sistem Idealde Hangi Davranisi Vermeli

Kullanici deneyimi acisindan ideal sistem sunlari yapmalidir:

1. Kayit baslar baslamaz kullaniciya durum gostermeli.
2. "Mikrofon calisiyor mu" ve "speech modeli hazir mi" farkini ayirmali.
3. Vision hatali olsa bile transcript calismaya devam etmeli.
4. Transcript paneli partial ve final farkini net gostermeli.
5. Kisa cevaplarda bile anlamsiz sessizlik yerine tanisal bilgi vermeli.
6. Final transcript backend'e guvenli ve idempotent bicimde yazilmali.
7. Oturum sonunda finalize, eksik veri olsa bile kontrollu davranmali.
8. Rapor, transcript ve metric eventlerden beslenmeli; sadece tek kaynaga bagli olmamali.

---

## 7. Bugun Neden Hedeflenen Davranisa Ulasamiyoruz

Bu kisim mevcut sistemin neden "teoride dogru, pratikte zayif" kaldigini anlatir.

### 7.1 Model secimi kaliteyi sinirliyor

Aktif speech modeli `tiny` oldugunda:

- Turkce dogruluk duser,
- teknik Ingilizce terimler bozulur,
- baglam kaybi artar,
- anlamsiz transcript olasiligi yukselir.

Bu proje bilgisayar muhendisligi mulakatlarina odaklandigi icin:

- `API`
- `thread`
- `pointer`
- `binary tree`
- `cache`
- `latency`
- `throughput`

gibi terimler kritik onemdedir.

Kucuk model bu terimleri cogu zaman koruyamaz.

### 7.2 VAD ve stream esikleri hassas

Speech-service tarafinda transcript her gelen sese gore degil, "anlamli konusma" sandigi parcalara gore decode edilir.

Bu iyi bir fikir olsa da su durumlarda sistem zayiflayabilir:

- kisa cevaplar,
- dusuk ses,
- mikrofon kalitesizligi,
- arka plan gurultusu,
- teknik kelimelerin kisa patlamalar halinde soylenmesi.

Sonuc:

- audio geliyor gibi gorunur,
- ama decode kuyruguna yeterince job dusmez,
- kullanici "sistem duymuyor" hissine kapilir.

### 7.3 Final transcript gec olusuyor

Final satirlar genelde kisa bir duraksama sonrasinda uretilir.

Bu nedenle:

- kullanici uzun sure tek nefeste konusursa,
- partial gorunse bile final satirlar gec gelebilir,
- panel bos veya eksik gibi algilanabilir.

### 7.4 Mixed language problemi var

Mulakat dili Turkce oldugunda sistemin decode dili de Turkce agirlikli gider.

Bu durumda Turkce cumle icine giren teknik Ingilizce terimler:

- Turkcelestirilebilir,
- bozulabilir,
- yaklasik benzer ama yanlis kelimelere donusebilir.

Bu bir "dil paketi eksik" sorunu olmaktan cok:

- model kapasitesi,
- decode tercihleri,
- mixed-language domain ihtiyaci

ile ilgilidir.

### 7.5 Segment filtreleme bazen faydali ama agresif olabilir

Backend tarafinda dusuk guvenli segmentler eleniyor.

Bu gurultu azaltmak icin iyidir, ancak:

- kisa teknik ifadeler,
- tek kelimelik onemli yanitlar,
- terim yogun cevaplar

yanlislikla dusurulebilir.

### 7.6 Sistemde iki transcript yolu var, ama uyum her yerde esit degil

Projede:

- canli websocket transcript yolu,
- ve soru gecisinde kullanilan offline transcription yolu

beraber yasiyor.

Bu iki yolun her zaman birebir uyumlu olmamasi, kullanicinin "bir yerde calisiyor, baska yerde bozuluyor" deneyimi yasamasina neden olabilir.

### 7.7 Readiness ile gercek kullanilabilirlik ayni sey degil

Bir servis ayakta olabilir, fakat:

- model yeni yukleniyor olabilir,
- warmup suruyor olabilir,
- kapasite dolu olabilir,
- kalite olarak hala yetersiz olabilir.

Bu nedenle "container up" olmak tek basina "sistem hazir" anlamina gelmez.

---

## 8. Yapmak Istedigimiz Sistem

Hedefledigimiz sistem bugunku yapinin tamamen degismesi degil, ayni mimarinin daha guvenilir ve daha kaliteli hale getirilmesidir.

### 8.1 Hedef 1: Transcript'in gercekten kullanisli olmasi

Istedigimiz transcript sistemi:

- kullanicinin konustugunu daha dogru anlamali,
- teknik TR+EN terimleri korumali,
- anlamsiz uydurma ciktilari azaltmali,
- partial ve final akisinda daha tutarli davranmali.

Bunun icin ana yon:

- once benchmark ile olcmek,
- sonra `tiny -> small -> medium` karsilastirmasi yapmak,
- en iyi kalite/gecikme dengesini secmektir.

### 8.2 Hedef 2: Audio-only fallback'in her zaman calismasi

Vision modulu bozulsa bile sistemin transcript tarafi calismaya devam etmelidir.

Yani:

- MediaPipe hata verirse mulakat tamamen durmamali,
- en azindan ses, transcript ve backend event akisi devam etmelidir.

### 8.3 Hedef 3: Teshis edilebilirlik

Sistem "neden transcript gelmedi" sorusuna cevap verebilmelidir.

Ornek tanilar:

- speech service ulasilamiyor,
- model hazir degil,
- audio chunk geliyor ama decode olmuyor,
- partial var ama final bekleniyor,
- VAD sesi speech olarak gormuyor,
- model yavas ama calisiyor.

Bu sayede kullanici da gelistirici de kör kalmaz.

### 8.4 Hedef 4: Batch ingestion'in temiz kalmasi

Canli transcript ve metric eventler backend'e:

- idempotent,
- tekrar denemeye dayanikli,
- merge kurallari belirli

sekilde yazilmaya devam etmelidir.

Sistem iyilestirilirken bu veri duzeni bozulmamali.

### 8.5 Hedef 5: Final raporun daha guvenilir olmasi

Finalize mantigi transcript ve metric eventlere dayaniyor.

Bu nedenle transcript ne kadar iyi olursa:

- rapor,
- pattern tespiti,
- LLM coaching,
- evidence summary

o kadar dogru olur.

Yani transcript kalitesi sadece alt modulu degil, rapor kalitesini de dogrudan etkiler.

---

## 9. Hedef Mimari Davranis

Yapmak istedigimiz sistemde ideal akisin su sekilde olmasi beklenir:

### 9.1 Baslatma

- Docker Compose ile tum servisler kalkar.
- API health cevap verir.
- Frontend erisilebilir olur.
- Speech-service `/health/ready` ile hazirligini bildirir.
- Model isiniyorsa bu durum acikca gorunur.

### 9.2 Session baslangici

- Kullanici session'a girer.
- Sorular yuklenir.
- Vision ve audio birbirinden gevsek bagli sekilde hazirlanir.
- Vision gecikse bile audio transcript engellenmez.

### 9.3 Canli akisin kurulmasi

- Mikrofon stream'i alinir.
- Websocket speech-service'e baglanir.
- Audio chunk akisi baslar.
- UI baglanti durumunu gosterir.
- Ilk partial ve ilk final zamanlari izlenir.

### 9.4 Transcript kalite davranisi

- Kisa cevaplar makul gecikmeyle parse edilir.
- Teknik terimler mumkun oldugunca korunur.
- Partial'lar kullaniciya canlilik hissi verir.
- Final'lar kalici transcript olarak backend'e yazilir.
- Sessizlik veya decode stall durumunda panel acikca nedenini anlatir.

### 9.5 Session sonu

- Son transcript parcasi sisteme ulasir.
- Finalize cagrisi yapilir.
- Score, feedback ve rapor uretilir.
- Kullanici rapor ekranina gecer.

---

## 10. Sistem Icinde Kritik Veri Turleri

Bu sistemi anlamak icin asagidaki veri tipleri onemlidir.

### 10.1 Session

Oturumun ana kimligidir.

Tasiyabilecegi bilgiler:

- session id,
- secilen rol,
- secilen dil,
- durum,
- olusturma zamani,
- score card baglantisi.

### 10.2 Metric Event

Real-time davranis veya analiz olayidir.

Ornek kaynaklar:

- vision,
- audio,
- llm,
- system.

Event mantigi timeline tabanlidir.

### 10.3 Transcript Segment

Konusma metninin zaman damgali parcasi.

Tipik alanlar:

- client segment id,
- start ms,
- end ms,
- text,
- confidence.

Bu segmentler backend'de merge edilir.

### 10.4 Score Card

Oturumun ozet puan kartidir.

### 10.5 Feedback Item

Belirli bir zaman araligina veya davranis kalibina bagli aciklama/uyaridir.

---

## 11. Sistem Tasariminda Temel Prensipler

Yapmak istedigimiz sistem su prensiplere sadik kalmalidir:

### 11.1 Dayaniklilik

Bir modül bozuldu diye tum oturum cop olmamali.

Ornek:

- vision bozulsa da audio kalmali,
- transcript gecikse de event akisi surmeli,
- LLM yoksa temel rapor yine cikmali.

### 11.2 Gozlemlenebilirlik

Sistemde "neden bozuldu" sorusu log, health, metrics ve UI tanilari ile cevaplanabilmeli.

### 11.3 Idempotency

Ayni event veya transcript segmenti tekrar gonderildiginde sistem bozulmamali.

### 11.4 Ayrik sorumluluklar

- frontend toplar ve gosterir,
- speech-service transcript uretir,
- backend kalici kayit ve rapor üretir.

Bu ayrim korunmalidir.

### 11.5 Kalite once olculmeli

Transcript model kalitesi hisle degil benchmark ile degerlendirilmelidir.

Bu nedenle model degisikligi rastgele degil, kontrollu benchmark ile yapilmalidir.

---

## 12. Gelistirme ve Debug Acisindan Ne Anlama Geliyor

Bu projede bir sorun ararken once su sorular sirayla cevaplanmalidir:

1. Session olusuyor mu?
2. Frontend mikrofon ve kamerayi alabiliyor mu?
3. Speech-service `/health/ready` gercekten hazir mi?
4. Websocket baglaniyor mu?
5. Audio chunk gidiyor mu?
6. VAD decode job uretiyor mu?
7. Partial geliyor mu?
8. Final segment geliyor mu?
9. Final segment backend'e yaziliyor mu?
10. Finalize transcript ve eventleri goruyor mu?

Bu sira, sistemin debug edilmesinde en saglikli zihinsel modeldir.

---

## 13. Kisa Ozet

Bu sistemin ozunde su vardir:

- frontend mulakati yonetir,
- speech-service sesi canli transcript'e cevirir,
- backend event ve transcript verisini saklar,
- finalize asamasi bunlari rapora donusturur.

Bugunku en buyuk mesele mimarinin tamamen yanlis olmasi degil; transcript kalitesi ve transcript akisinin gorunurlugunun yeterince guvenilir olmamasidir.

Yapmak istedigimiz sey:

- mevcut mimariyi koruyup,
- transcript hattini daha dogru,
- daha acik tanilanabilir,
- daha mixed-language uyumlu,
- daha dayanikli

hale getirmektir.

Bu belgeyi sistemde yeni degisiklik yaparken "ne var, ne olmali, neden oyle olmali" rehberi olarak kullanabiliriz.
