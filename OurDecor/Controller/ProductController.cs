using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OurDecor.Models;

namespace OurDecor.Controller
{

    public class ProductDTO
    {
        public int Id { get; set; }
        public string? TypeProduct { get; set; }
        public string? NameProduct { get; set; }
        public int Article { get; set; }
        public decimal MinPricePartner { get; set; }
        public decimal WidthRoll { get; set; }
    }

    [Route("api/products")]
    [ApiController]
    public class ProductController : ControllerBase
    {

        public AppDbContext _context { get; set; }

        public ProductController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("Get")]
        public async Task<ActionResult<IEnumerable<ProductDTO>>> Get()
        {
            var result = await _context.Products
                .Select(e => new ProductDTO
                {
                    Id = e.Id,
                    TypeProduct = e.ProductType.TypeProduct,
                    NameProduct = e.NameProduct,
                    Article = e.Article,
                    MinPricePartner = e.ProductMaterialsImports.Sum(em => em.QuantityMaterial * em.Material.UnitPrice),
                    WidthRoll = e.WidthRoll
                }).ToListAsync();
                
            return Ok(result);
        }

        [HttpGet("GetId/{id}")]
        public IActionResult GetId(int id)
        {
            var result = _context.Products.FirstOrDefaultAsync(e => e.Id == id);

            if (result == null)
                return NotFound();

            return Ok(result);
        }

        [HttpGet("product-types")]
        public async Task<ActionResult<IEnumerable<ProductTypeImport>>> GetProductTypes()
        {
            var types = await _context.ProductType.ToListAsync();
            return Ok(types);
        }

        // POST api/<ProductController>
        [HttpPost("Post")]
        public async Task<IActionResult> Post([FromBody] ProductDTO product)
        {
            if(!ModelState.IsValid)
                return BadRequest(ModelState);

            var prod = new ProductsImport
            {
                TypeProduct = product.TypeProduct,
                NameProduct = product.NameProduct,
                Article = product.Article,
                MinPricePartner = product.MinPricePartner,
                WidthRoll = product.WidthRoll
            };

            _context.Products.Add(prod);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetId), new { id = prod.Id}, prod);
        }

        // PUT api/<ProductController>/5
        [HttpPut("Put/{id}")]
        public async Task<IActionResult> Put(int id, [FromBody] ProductDTO value)
        {
            var result = await _context.Products.FindAsync(id);

            if (result == null)
                return NotFound();

            result.TypeProduct = value.TypeProduct;
            result.NameProduct = value.NameProduct;
            result.Article = value.Article;
            result.MinPricePartner = value.MinPricePartner;
            result.WidthRoll = value.WidthRoll;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // DELETE api/<ProductController>/5
        [HttpDelete("Delete/{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var result = _context.Products.Find(id);
            if (result == null)
                return NotFound();

            _context.Products.Remove(result);
            await _context.SaveChangesAsync();

            return NoContent();

        }
    }
}
