using DestinaYeniVersiyon.App_Classes;
using DestinaYeniVersiyon.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Reflection;
using System.Web.Http;


namespace DestinaApi.Controllers
{
    // Çağrılar: /api/TrackApi/Start?o=...&c=...
    public class TrackApiController : ApiController
    {
        private readonly DestinaEntity db = new DestinaEntity();

        public class LocDto { public decimal lat { get; set; } public decimal lon { get; set; } }

        [HttpPost]
        public IHttpActionResult Start(string o, string c, [FromBody] LocDto dto)
        {
            if (dto == null)
                return BadRequest("Location data is missing.");

            var rez = db.rezervasyonlar.FirstOrDefault(x => x.orderid == o);
            if (rez == null) return NotFound();
            if (rez.tracking_status == 2) return BadRequest(); // bitmiş
            if (rez.tracking_driver_token != c) return Unauthorized();

            rez.tracking_status = 1;
            rez.tracking_started_at = DateTime.UtcNow;
            rez.tracking_start_lat = (double)dto.lat;
            rez.tracking_start_lon = (double)dto.lon;
            rez.tracking_ended_at = null;

            db.ride_locations.Add(new ride_locations
            {
                orderid = o,
                lat = dto.lat,
                lon = dto.lon,
                ts = DateTime.UtcNow
            });

            db.SaveChanges();
            return Ok();
        }


        [HttpPost]
        public IHttpActionResult Location(string o, string c, LocDto dto)
        {
            var rez = db.rezervasyonlar.FirstOrDefault(x => x.orderid == o);
            if (rez == null) return NotFound();
            if (rez.tracking_status != 1) return BadRequest();
            if (rez.tracking_driver_token != c) return Unauthorized();

            // Son nokta
            var last = db.ride_locations.Where(x => x.orderid == o)
                                        .OrderByDescending(x => x.ts)
                                        .Select(x => new { x.lat, x.lon, x.ts })
                                        .FirstOrDefault();

            // (A) Çok sık ping -> atla (ör. 5s altı ise)
            if (last?.ts != null && (DateTime.UtcNow - last.ts.Value).TotalSeconds < 5)

                return Ok(); // kabul et, ama yeni kayıt yazma

            // (B) Hareket yok -> atla (ör. 20 m altı)
            if (last != null)
            {
                double dMeters = HaversineMeters((double)last.lat, (double)last.lon, (double)dto.lat, (double)dto.lon);
                if (dMeters < 20) return Ok();
            }

            db.ride_locations.Add(new ride_locations
            {
                orderid = o,
                lat = dto.lat,
                lon = dto.lon,
                ts = DateTime.UtcNow
            });
            db.SaveChanges();
            return Ok();
        }

        private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000; // meters
            double toRad(double x) => x * Math.PI / 180.0;
            var dLat = toRad(lat2 - lat1);
            var dLon = toRad(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(toRad(lat1)) * Math.Cos(toRad(lat2)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        [HttpPost]
        public IHttpActionResult Stop(string o, string c, [FromBody] LocDto dto)
        {
            var rez = db.rezervasyonlar.FirstOrDefault(x => x.orderid == o);
            if (rez == null) return NotFound();
            if (rez.tracking_status != 1) return BadRequest(); // aktif değil
            if (rez.tracking_driver_token != c) return Unauthorized();

            rez.tracking_status = 2;
            rez.tracking_ended_at = DateTime.UtcNow;
            rez.tracking_end_lat = (double)dto.lat;
            rez.tracking_end_lon = (double)dto.lon;

            db.ride_locations.Add(new ride_locations
            {
                orderid = o,
                lat = dto.lat,
                lon = dto.lon,
                ts = DateTime.UtcNow
            });

            db.SaveChanges();
            return Ok();
        }


        [HttpGet]
        public IHttpActionResult History(string o, string c)
        {
            var rez = db.rezervasyonlar.FirstOrDefault(x => x.orderid == o);
            if (rez == null) return NotFound();

            // viewer veya driver token görebilir
            if (rez.tracking_view_token != c && rez.tracking_driver_token != c) return Unauthorized();

            var pts = db.ride_locations
                        .Where(x => x.orderid == o)
                        .OrderBy(x => x.id) // ✅ kesin sıralama için id
                        .Select(x => new { x.ts, x.lat, x.lon })
                        .ToList();

            return Ok(new
            {
                status = rez.tracking_status,
                started_at = rez.tracking_started_at,
                ended_at = rez.tracking_ended_at,
                points = pts
            });
        }


        private static T TryGetProp<T>(object obj, params string[] names)
        {
            if (obj == null) return default(T);
            var t = obj.GetType();
            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (p != null && p.CanRead)
                {
                    var val = p.GetValue(obj);
                    if (val == null) continue;
                    try
                    {
                        if (typeof(T) == typeof(string)) return (T)(object)val.ToString();
                        if (typeof(T).IsAssignableFrom(val.GetType())) return (T)val;
                        return (T)System.Convert.ChangeType(val, typeof(T));
                    }
                    catch { }
                }
            }
            return default(T);
        }

        [HttpGet]
        public IHttpActionResult Meta(string o)
{
    var rez = db.rezervasyonlar.FirstOrDefault(x => x.orderid == o);
    if (rez == null) return NotFound();

    // --- 1) Vehicle / Driver (aynı kalsın) ---
    var rezId = TryGetProp<int?>(rez, "id", "Id", "ID");
    rezervasyonbilgileri atama = null;
    if (rezId.HasValue)
        atama = db.rezervasyonbilgileri.FirstOrDefault(x => x.rezervasyonid == rezId.Value);
    if (atama == null)
        atama = db.rezervasyonbilgileri.FirstOrDefault(x => x.rezervasyonlar != null && x.rezervasyonlar.orderid == o);

    string plate = null, driverName = null, driverPhone = null;
    try
    {
        if (atama != null && !string.IsNullOrWhiteSpace(atama.aracbilgileri))
            plate = JsonConvert.DeserializeObject<CarInfo>(atama.aracbilgileri)?.plate_number;
    } catch {}
    try
    {
        if (atama != null && !string.IsNullOrWhiteSpace(atama.soforbilgilerijson))
        {
            var drv = JsonConvert.DeserializeObject<DriverInfo>(atama.soforbilgilerijson);
            driverName  = string.Join(" ", new[] { drv?.first_name, drv?.last_name }.Where(s => !string.IsNullOrWhiteSpace(s)));
            driverPhone = drv?.phone;
        }
    } catch {}

    // --- 2) Booking JSON'ları ---
    var requestJsonStr  = TryGetProp<string>(rez, "bookrequestjson", "book_request_json", "requestjson", "booking_request", "bookjson");
    var responseJsonStr = TryGetProp<string>(rez, "bookresponsejson", "book_response_json", "responsejson", "booking_response");

    var customers = new System.Collections.Generic.List<string>();
    DateTime? reservationDt = null;

    // Customers
    if (!string.IsNullOrWhiteSpace(requestJsonStr))
    {
        try
        {
            var req = JObject.Parse(requestJsonStr);
            var mp = req.SelectToken("main_passenger");
            if (mp != null)
            {
                var fn = (string)mp["first_name"];
                var mn = (string)mp["middle_name"];
                var ln = (string)mp["last_name"];
                var full = string.Join(" ", new[] { fn, mn, ln }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(full)) customers.Add(full);
            }
        } catch {}
    }
            string reservationStr = null;

            // Reservation datetime
            if (!string.IsNullOrWhiteSpace(responseJsonStr))
            {
                try
                {
                    var resp = JObject.Parse(responseJsonStr);
                    // JSON’daki start_time / reservation_time neyse onu aynen al
                    reservationStr = (string)(resp.SelectToken("start_time") ?? resp.SelectToken("reservation_time"));
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(reservationStr))
                // DB’deki "Tarih" vb. kolonlardan gelen format neyse aynen al
                reservationStr = TryGetProp<string>(rez, "Tarih", "tarih", "RezervasyonTarihi", "reservation_datetime", "Date", "CreatedAt");
            if (string.IsNullOrWhiteSpace(reservationStr))
                // Son çare: tracking_started_at varsa ISO’sunu ver (dönüşüm yapmıyoruz)
                reservationStr = TryGetProp<DateTime?>(rez, "tracking_started_at")?.ToString("s");


            if (customers.Count == 0)
    {
        var singleName = TryGetProp<string>(rez, "MusteriAdiSoyadi", "musteri_ad_soyad", "musteriadsoyadi", "CustomerName", "AdSoyad", "AdiSoyadi", "name");
        if (!string.IsNullOrWhiteSpace(singleName)) customers.Add(singleName);
    }

    // --- 3) FROM / TO adresleri ---
    string fromAddress = null, toAddress = null;
    if (!string.IsNullOrWhiteSpace(requestJsonStr))
    {
        try
        {
            var req = JObject.Parse(requestJsonStr);
            fromAddress = (string)(req.SelectToken("start_point.address") ?? req.SelectToken("from.address") ?? req.SelectToken("pickup.address"));
            toAddress   = (string)(req.SelectToken("end_point.address")   ?? req.SelectToken("to.address")   ?? req.SelectToken("dropoff.address"));
        } catch {}
    }
    // DB kolon fallback’leri (senin isimlerine uyacak şekilde)
    if (string.IsNullOrWhiteSpace(fromAddress))
        fromAddress = TryGetProp<string>(rez, "Nereden", "From", "from_address", "start_address");
    if (string.IsNullOrWhiteSpace(toAddress))
        toAddress   = TryGetProp<string>(rez, "Nereye", "To", "to_address", "end_address");

    // --- 4) Tracking core ---
    var startedAt = TryGetProp<DateTime?>(rez, "tracking_started_at");
    var endedAt   = TryGetProp<DateTime?>(rez, "tracking_ended_at");
    var viewToken = TryGetProp<string>(rez, "tracking_view_token");

    return Ok(new
    {
        orderid = rez.orderid,
        customers = customers,                 // array
        reservation_datetime = reservationStr,  // 🔥 string, olduğu gibi
        started_at = startedAt,
        ended_at = endedAt,

        // NEW
        from_address = fromAddress,
        to_address   = toAddress,

        plate = plate,
        driver_name = driverName,
        driver_phone = driverPhone,
        view_token = viewToken
    });
}


        [HttpPost]
        public IHttpActionResult Ping(string o, string c)
        {
            return Ok($"Ping received: {o} - {c}");
        }
    }
}
