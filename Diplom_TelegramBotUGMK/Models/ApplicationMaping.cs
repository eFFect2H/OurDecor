using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    [Table("ApplicationMappings")]
    public class ApplicationMapping
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int MainRowId { get; set; }

        [Required]
        public int DirectionRowId { get; set; }

        [Required]
        [MaxLength(255)]
        public string Direction { get; set; }

        [MaxLength(255)]
        public string DirectionSpreadsheetId { get; set; }

        [MaxLength(255)]
        public string DirectionSheetName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}
