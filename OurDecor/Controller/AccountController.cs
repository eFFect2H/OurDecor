using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OurDecor.Models;
using System.Security.Claims;


namespace OurDecor.Controller
{
    public static class PasswordHelper
    {
        public static string CreatePasswordHash(string password) =>
            BCrypt.Net.BCrypt.HashPassword(password);

        public static bool VerifyPassword(string password, string hash) =>
            BCrypt.Net.BCrypt.Verify(password, hash);
    }

    [ApiController]
    [Route("api/account")]
    public class AccountController : ControllerBase
    {
        private readonly AppDbContext _context;

        public AccountController(AppDbContext context)
        {
            _context = context;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] User user)
        {
            if (await _context.Users.AnyAsync(u => u.Login == user.Login))
                return BadRequest("Пользователь уже существует.");

            var hash = PasswordHelper.CreatePasswordHash(user.PasswordHash);

            var userobj = new User
            {
                Login = user.Login,
                PasswordHash = hash,
                RoleId = user.RoleId
            };

            _context.Users.Add(userobj);
            await _context.SaveChangesAsync();

            return Ok("Регистрация прошла успешно.");
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] UserDTO user)
        {
            var userInf = await _context.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Login == user.Login);

            if (userInf == null)
                return Unauthorized("Неверный логин или пароль.");

            var isValid = PasswordHelper.VerifyPassword(user.PasswordHash, userInf.PasswordHash);
            if (!isValid)
                return Unauthorized("Неверный логин или пароль.");

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, userInf.Login),
                new Claim(ClaimTypes.Role, userInf.Role.NameRole)
            };

            var identity = new ClaimsIdentity(claims, "Cookies");
            await HttpContext.SignInAsync(new ClaimsPrincipal(identity));

            return Ok($"Вход выполнен. Роль: {userInf.Role.NameRole}");
        }

    }

    public class UserDTO
    {
        public string Login { get; set; }
        public string PasswordHash { get; set; } = null!;
    }
}
