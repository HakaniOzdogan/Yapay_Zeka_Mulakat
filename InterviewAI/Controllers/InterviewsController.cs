using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InterviewAI.Data;
using InterviewAI.Models;

namespace InterviewAI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class InterviewsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public InterviewsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Interview>>> GetInterviews()
        {
            return await _context.Interviews
                .Include(i => i.Questions)
                .Include(i => i.Feedbacks)
                .ToListAsync();
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Interview>> GetInterview(int id)
        {
            var interview = await _context.Interviews
                .Include(i => i.Questions)
                .Include(i => i.Feedbacks)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (interview == null)
                return NotFound();

            return interview;
        }

        [HttpGet("user/{userId}")]
        public async Task<ActionResult<IEnumerable<Interview>>> GetUserInterviews(int userId)
        {
            return await _context.Interviews
                .Where(i => i.UserId == userId)
                .Include(i => i.Questions)
                .Include(i => i.Feedbacks)
                .ToListAsync();
        }

        [HttpPost]
        public async Task<ActionResult<Interview>> CreateInterview(Interview interview)
        {
            _context.Interviews.Add(interview);
            await _context.SaveChangesAsync();
            return CreatedAtAction("GetInterview", new { id = interview.Id }, interview);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateInterview(int id, Interview interview)
        {
            if (id != interview.Id)
                return BadRequest();

            _context.Entry(interview).State = EntityState.Modified;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!_context.Interviews.Any(i => i.Id == id))
                    return NotFound();
                throw;
            }
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteInterview(int id)
        {
            var interview = await _context.Interviews.FindAsync(id);
            if (interview == null)
                return NotFound();

            _context.Interviews.Remove(interview);
            await _context.SaveChangesAsync();
            return NoContent();
        }
    }
}
