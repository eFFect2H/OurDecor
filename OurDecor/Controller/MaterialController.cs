using Microsoft.AspNetCore.Mvc;
using OurDecor.Models;
using System.Threading.Tasks;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace OurDecor.Controller
{
    public class MaterialDTO
    {
        public int Id { get; set; }
        public string? NameMaterial { get; set; }
        public string? TypeMaterial { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal QuantityStock { get; set; }
        public decimal MinQuantity { get; set; }
        public int QuantityPackage { get; set; }
        public string? Metering { get; set; }
        public int? MaterialTypeId { get; set; }
    }


    [Route("api/Material")]
    [ApiController]
    public class MaterialController : ControllerBase
    {
        public AppDbContext _context { get; set; }

        public MaterialController(AppDbContext context)
        {
            _context = context;
        }

        // GET: api/<MaterialController>
        [HttpGet("Get")]
        public IActionResult Get()
        {
            var result = _context.Material
                .Select(e => new MaterialDTO
                {
                    Id = e.Id,
                    NameMaterial = e.NameMaterial,
                    TypeMaterial = e.TypeMaterial,
                    UnitPrice = e.QuantityStock,
                    QuantityStock = e.QuantityStock,
                    MinQuantity = e.MinQuantity,
                    QuantityPackage = e.QuantityPackage,
                    Metering = e.Metering

                }).ToList();
                
            return Ok(result);
        }

        // GET api/<MaterialController>/5
        [HttpGet("GetId/{id}")]
        public async Task<IActionResult> GetId(int id)
        {
            var result = await _context.Material.FindAsync(id);
            if (result == null)
                return NotFound();

            return Ok(result);
        }

        // POST api/<MaterialController>
        [HttpPost("Post")]
        public async Task<IActionResult> PostAsync([FromBody] MaterialDTO value)
        {
            if(!ModelState.IsValid)
                return BadRequest(ModelState);


            var material = new MaterialImport
            {
                NameMaterial = value.NameMaterial,
                TypeMaterial = value.TypeMaterial,
                UnitPrice = value.UnitPrice,
                QuantityStock = value.QuantityStock,
                MinQuantity = value.MinQuantity,
                QuantityPackage = value.QuantityPackage,
                Metering = value.Metering,
                MaterialTypeId = value.MaterialTypeId
                
            };

            _context.Material.Add(material);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetId), new {id = material.Id}, material);
        }

        // PUT api/<MaterialController>/5
        [HttpPut("Put/{id}")]
        public async Task<IActionResult> Put(int id, [FromBody] MaterialDTO value)
        {
            var result = _context.Material.Find(id);
            if(result == null)
                return NoContent();

            result.NameMaterial = value.NameMaterial;
            result.TypeMaterial = value.TypeMaterial;
            result.UnitPrice = value.UnitPrice;
            result.QuantityStock = value.QuantityStock;
            result.MinQuantity = value.MinQuantity;
            result.QuantityPackage = value.QuantityPackage;
            result.Metering = value.Metering;
            result.MaterialTypeId = value.MaterialTypeId;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // DELETE api/<MaterialController>/5
        [HttpDelete("Delete/{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var material = _context.Material.Find(id);
            if(material == null)
                return NotFound();

            _context.Material.Remove(material);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
