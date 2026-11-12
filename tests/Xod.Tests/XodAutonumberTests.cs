// An autonumber value is handed out once (ISS-052). The next value is above every value the table has
// held: rows still there, and rows deleted since, whose highest value the table records when they go.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Xod.Tests
{
    public class NumberedItem
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class NumberedLong
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    // An autonumber that no index covers: its highest value is read from the rows.
    public class SequencedNote
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true)] public int Seq { get; set; }
    }

    // ISS-047: a seed and an increment set on the attribute.
    public class SeededTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = 1000, IdentityIncrement = 5)] public int Number { get; set; }
    }

    // A seed beyond the range of int, on a long key.
    public class SeededLongTicket
    {
        [PrimaryKey, Property(AutoNumber = true, IdentitySeed = 5_000_000_000L, IdentityIncrement = 10)] public long Id { get; set; }
        public string Name { get; set; }
    }

    public class ZeroIncrementTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentityIncrement = 0)] public int Number { get; set; }
    }

    public class FractionalSeedTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = 1.5)] public int Number { get; set; }
    }

    public class TextSeedTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = "ten")] public int Number { get; set; }
    }

    public class OversizedSeedTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = 5_000_000_000L)] public int Number { get; set; }
    }

    // Review of TASK-062: seeds that would hand out 0 (the value that means "unset"), or a number the type can't
    // hold exactly, and a seed on a Guid, which has no numbers.
    public class ZeroSeedTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = 0)] public int Number { get; set; }
    }

    public class NegativeSeedTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = -1)] public int Number { get; set; }
    }

    public class FloatTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = 16_777_216)] public float Number { get; set; }
    }

    public class InexactDoubleSeedTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = 9_007_199_254_740_993L)] public double Number { get; set; }
    }

    public class GuidSeedTicket
    {
        [PrimaryKey] public string Key { get; set; }
        [Property(AutoNumber = true, IdentitySeed = 5)] public Guid Token { get; set; }
    }

    // No key: an update by example changes every row that matches.
    public class SequencedTag
    {
        [Property(AutoNumber = true)] public int Seq { get; set; }
        public string Label { get; set; }
    }

    // An autonumber key and a value stored as attributes: an update filter used to drop both.
    public class AttributeKeyed
    {
        [PrimaryKey, Property(AutoNumber = true, Position = ValuePosition.Attribute)] public int Id { get; set; }
        [Property(Position = ValuePosition.Attribute)] public string Tag { get; set; }
        public string Name { get; set; }
    }

    public class FilteredContact
    {
        public int Id { get; set; }
        [UniqueKey] public string Email { get; set; }
        public string Name { get; set; }
    }

    public class XodAutonumberTests
    {
        public static IEnumerable<object[]> Encodings() => new[]
        {
            new object[] { StorageFormat.Xml, null, true },
            new object[] { StorageFormat.Json, null, true },
            new object[] { StorageFormat.Binary, null, true },
            new object[] { StorageFormat.Xml, "pw", true },
            new object[] { StorageFormat.Xml, null, false }, // no index: the other delete path
        };

        private static string NewDatabase(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "xod-autonumber", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.xod");
        }

        private static XodContext Open(string path, string password, StorageFormat format, bool indexing) =>
            new XodContext(path, password, new DatabaseOptions { StorageFormat = format, EnableIndexing = indexing });

        [Theory]
        [MemberData(nameof(Encodings))]
        public void A_deleted_highest_id_is_not_handed_out_again(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("highest");
            using (var db = Open(path, password, format, indexing))
            {
                for (int i = 0; i < 5; i++) db.Insert(new NumberedItem { Name = "n" + i });
                Assert.True(db.Delete(db.Find<NumberedItem>(x => x.Id == 5)));
            }
            using (var db = Open(path, password, format, indexing))
                Assert.Equal(6, db.Insert(new NumberedItem { Name = "after" }).Id);
        }

        [Theory]
        [MemberData(nameof(Encodings))]
        public void Emptying_a_table_does_not_restart_its_numbering(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("empty");
            using (var db = Open(path, password, format, indexing))
            {
                for (int i = 0; i < 3; i++) db.Insert(new NumberedLong { Name = "n" + i });
                foreach (var row in db.Select<NumberedLong>().ToList()) Assert.True(db.Delete(row));
                Assert.Equal(0, db.Count<NumberedLong>());
            }
            using (var db = Open(path, password, format, indexing))
            {
                Assert.Equal(0, db.Count<NumberedLong>());
                Assert.Equal(4L, db.Insert(new NumberedLong { Name = "after" }).Id);
            }
        }

        // The last row isn't the highest when a lower Id was given explicitly after a higher one.
        [Theory]
        [MemberData(nameof(Encodings))]
        public void The_next_id_is_above_the_highest_row_not_the_last(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("explicit");
            using (var db = Open(path, password, format, indexing))
            {
                db.Insert(new NumberedItem { Id = 100, Name = "high" });
                db.Insert(new NumberedItem { Id = 7, Name = "low" });
            }
            using (var db = Open(path, password, format, indexing))
                Assert.Equal(101, db.Insert(new NumberedItem { Name = "next" }).Id);
        }

        // Deleting a row in another context than the one that numbered it: the mark covers the row.
        [Fact]
        public void A_delete_in_a_later_context_still_records_the_highest_value()
        {
            string path = NewDatabase("later");
            using (var db = new XodContext(path))
                for (int i = 0; i < 4; i++) db.Insert(new NumberedItem { Name = "n" + i });
            using (var db = new XodContext(path))
                Assert.True(db.Delete(db.Find<NumberedItem>(x => x.Id == 4)));
            using (var db = new XodContext(path))
                Assert.Equal(5, db.Insert(new NumberedItem { Name = "after" }).Id);
        }

        [Fact]
        public void An_autonumber_without_an_index_continues_after_its_highest_row_is_deleted()
        {
            string path = NewDatabase("unindexed");
            using (var db = new XodContext(path))
            {
                for (int i = 0; i < 3; i++) db.Insert(new SequencedNote { Key = "k" + i });
                Assert.Equal(new[] { 1, 2, 3 }, db.Select<SequencedNote>().Select(n => n.Seq).OrderBy(s => s));
                Assert.True(db.Delete(db.Find<SequencedNote>(n => n.Key == "k2")));
            }
            using (var db = new XodContext(path))
                Assert.Equal(4, db.Insert(new SequencedNote { Key = "k9" }).Seq);
        }
        // ISS-047: the numbering starts at the seed and rises by the increment, and goes on from there after a
        // reopen.
        [Theory]
        [MemberData(nameof(Encodings))]
        public void A_seed_and_an_increment_set_on_the_attribute_are_used(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("seeded");
            using (var db = Open(path, password, format, indexing))
            {
                for (int i = 0; i < 3; i++) db.Insert(new SeededTicket { Key = "k" + i });
                Assert.Equal(new[] { 1000, 1005, 1010 }, db.Select<SeededTicket>().Select(t => t.Number).OrderBy(n => n));
            }
            using (var db = Open(path, password, format, indexing))
                Assert.Equal(1015, db.Insert(new SeededTicket { Key = "after" }).Number);
        }

        [Fact]
        public void A_long_seed_beyond_the_range_of_int_is_used()
        {
            string path = NewDatabase("seeded-long");
            using var db = new XodContext(path);
            Assert.Equal(5_000_000_000L, db.Insert(new SeededLongTicket { Name = "a" }).Id);
            Assert.Equal(5_000_000_010L, db.Insert(new SeededLongTicket { Name = "b" }).Id);
        }

        // A seed or an increment that can't number the property is refused when the type is first used, not
        // when a number is handed out.
        [Fact]
        public void A_seed_or_an_increment_that_cannot_number_the_property_is_refused()
        {
            string path = NewDatabase("seeded-bad");
            using var db = new XodContext(path);
            Assert.Contains("IdentityIncrement", Assert.Throws<AutonumberSeedException>(() => db.Insert(new ZeroIncrementTicket { Key = "a" })).Message);
            Assert.Contains("IdentitySeed", Assert.Throws<AutonumberSeedException>(() => db.Insert(new FractionalSeedTicket { Key = "a" })).Message);
            Assert.Contains("IdentitySeed", Assert.Throws<AutonumberSeedException>(() => db.Insert(new TextSeedTicket { Key = "a" })).Message);
            Assert.Contains("IdentitySeed", Assert.Throws<AutonumberSeedException>(() => db.Insert(new OversizedSeedTicket { Key = "a" })).Message);
        }
        // ISS-059: an update that raises an autonumber value above the counter moves the counter past it, so the
        // next insert isn't handed a value a row already holds (for a key, the insert used to fail; for any other
        // autonumber it was a silent duplicate).
        [Theory]
        [MemberData(nameof(Encodings))]
        public void An_update_that_raises_an_autonumber_key_moves_the_counter_past_it(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("raise-key");
            using var db = Open(path, password, format, indexing);
            for (int i = 0; i < 3; i++) db.Insert(new NumberedItem { Name = "n" + i });
            Assert.True(db.Update(db.Find<NumberedItem>(x => x.Id == 3), new NumberedItem { Id = 10, Name = "raised" }));
            Assert.Equal(11, db.Insert(new NumberedItem { Name = "next" }).Id);
        }

        [Theory]
        [MemberData(nameof(Encodings))]
        public void An_update_that_raises_an_autonumber_moves_the_counter_past_it(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("raise");
            using var db = Open(path, password, format, indexing);
            for (int i = 0; i < 3; i++) db.Insert(new SequencedNote { Key = "k" + i });
            Assert.True(db.Update(db.Find<SequencedNote>(n => n.Key == "k0"), new SequencedNote { Key = "k0", Seq = 10 }));
            Assert.Equal(11, db.Insert(new SequencedNote { Key = "k9" }).Seq);
        }

        // And one that lowers the highest value keeps a record of it, so it isn't handed out after a reopen.
        [Theory]
        [MemberData(nameof(Encodings))]
        public void An_update_that_lowers_the_highest_autonumber_key_does_not_hand_it_out_again(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("lower-key");
            using (var db = Open(path, password, format, indexing))
            {
                db.Insert(new NumberedItem { Id = 5, Name = "five" });
                db.Insert(new NumberedItem { Id = 9, Name = "nine" });
                Assert.True(db.Update(db.Find<NumberedItem>(x => x.Id == 9), new NumberedItem { Id = 6, Name = "six" }));
            }
            using (var db = Open(path, password, format, indexing))
                Assert.Equal(10, db.Insert(new NumberedItem { Name = "after" }).Id);
        }

        [Theory]
        [MemberData(nameof(Encodings))]
        public void An_update_that_lowers_the_highest_autonumber_does_not_hand_it_out_again(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("lower");
            using (var db = Open(path, password, format, indexing))
            {
                for (int i = 0; i < 3; i++) db.Insert(new SequencedNote { Key = "k" + i });
                Assert.True(db.Update(db.Find<SequencedNote>(n => n.Key == "k2"), new SequencedNote { Key = "k2", Seq = 1 }));
            }
            using (var db = Open(path, password, format, indexing))
                Assert.Equal(4, db.Insert(new SequencedNote { Key = "k9" }).Seq);
        }

        // An update whose new item leaves a key autonumber unset (0) keeps the row's value.
        [Theory]
        [MemberData(nameof(Encodings))]
        public void An_update_that_leaves_an_autonumber_key_unset_keeps_the_row_s_value(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("unset-key");
            using var db = Open(path, password, format, indexing);
            for (int i = 0; i < 2; i++) db.Insert(new NumberedItem { Name = "n" + i });
            bool updated = db.Update(db.Find<NumberedItem>(x => x.Id == 2), new NumberedItem { Name = "renamed" });
            Assert.True(updated);
            Assert.Equal("renamed", db.Find<NumberedItem>(x => x.Id == 2)?.Name);
            Assert.Equal(new[] { 1, 2 }, db.Select<NumberedItem>().Select(x => x.Id).OrderBy(x => x));
        }

        // ISS-083: a number handed out inside a transaction that is rolled back is gone: the caller holds it (the
        // insert gave it back), so it isn't handed out again, in this context or after reopening.
        [Fact]
        public void A_number_handed_out_in_a_rolled_back_transaction_is_not_handed_out_again()
        {
            string path = NewDatabase("rollback");
            using var db = new XodContext(path);
            db.Insert(new NumberedItem { Name = "a" });
            db.Insert(new NumberedItem { Name = "b" });
            using (var tx = db.BeginTransaction())
            {
                Assert.Equal(3, db.Insert(new NumberedItem { Name = "rolled back" }).Id);
                tx.Rollback();
            }
            Assert.Equal(4, db.Insert(new NumberedItem { Name = "after" }).Id);
        }

        [Fact]
        public void A_number_handed_out_in_a_transaction_the_close_rolls_back_is_not_handed_out_after_reopening()
        {
            string path = NewDatabase("rollback-reopen");
            using (var db = new XodContext(path))
            {
                db.Insert(new NumberedItem { Name = "a" });
                var tx = db.BeginTransaction();
                Assert.Equal(2, db.Insert(new NumberedItem { Name = "rolled back" }).Id);
                // Closed with the transaction open: the close rolls it back.
            }
            using (var db = new XodContext(path))
            {
                Assert.Equal(new[] { "a" }, db.Select<NumberedItem>().Select(x => x.Name));
                Assert.Equal(3, db.Insert(new NumberedItem { Name = "after" }).Id);
            }
        }

        // A table whose first rows were rolled back keeps the mark in a table with no pages.
        [Fact]
        public void A_number_handed_out_in_a_rolled_back_first_insert_is_not_handed_out_after_reopening()
        {
            string path = NewDatabase("rollback-first");
            using (var db = new XodContext(path))
            {
                using var tx = db.BeginTransaction();
                Assert.Equal(1, db.Insert(new NumberedItem { Name = "rolled back" }).Id);
                tx.Rollback();
            }
            using (var db = new XodContext(path))
            {
                Assert.Empty(db.Select<NumberedItem>());
                Assert.Equal(2, db.Insert(new NumberedItem { Name = "after" }).Id);
            }
        }

        // A drop inside the transaction resets the counter; the rollback brings the rows back, and their numbers
        // with them (review of TASK-063: the next number was one a row held). Keyed and unguarded alike.
        [Fact]
        public void A_rolled_back_drop_takes_its_counter_reset_with_it()
        {
            string path = NewDatabase("drop-rollback");
            using var db = new XodContext(path);
            for (int i = 0; i < 3; i++) db.Insert(new NumberedItem { Name = "n" + i });
            for (int i = 0; i < 3; i++) db.Insert(new SequencedNote { Key = "k" + i });
            using (var tx = db.BeginTransaction())
            {
                db.Drop<NumberedItem>();
                db.Drop<SequencedNote>();
                Assert.Equal(1, db.Insert(new NumberedItem { Name = "in tx" }).Id);
                Assert.Equal(1, db.Insert(new SequencedNote { Key = "in tx" }).Seq);
                tx.Rollback();
            }
            Assert.Equal(4, db.Insert(new NumberedItem { Name = "after" }).Id);
            Assert.Equal(4, db.Insert(new SequencedNote { Key = "after" }).Seq);
            Assert.Equal(new[] { 1, 2, 3, 4 }, db.Select<SequencedNote>().Select(n => n.Seq).OrderBy(s => s));
        }

        // A committed drop still resets the counter: the table is gone.
        [Fact]
        public void A_committed_drop_resets_its_counter()
        {
            string path = NewDatabase("drop-commit");
            using var db = new XodContext(path);
            for (int i = 0; i < 3; i++) db.Insert(new NumberedItem { Name = "n" + i });
            using (var tx = db.BeginTransaction())
            {
                db.Drop<NumberedItem>();
                tx.Commit();
            }
            Assert.Equal(1, db.Insert(new NumberedItem { Name = "after" }).Id);
        }

        // ---- Review of TASK-062 ----

        // The new item is the caller's: an update keeps each row's own value without writing it into the new
        // item, so one new item can serve several updates, and an update by example keeps every row's value.
        [Theory]
        [MemberData(nameof(Encodings))]
        public void One_new_item_used_for_several_updates_keeps_each_row_s_autonumber(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("reused-item");
            using var db = Open(path, password, format, indexing);
            for (int i = 0; i < 3; i++) db.Insert(new SequencedNote { Key = "k" + i });
            var template = new SequencedNote();
            for (int i = 0; i < 3; i++)
            {
                template.Key = "k" + i;
                Assert.True(db.Update(db.Find<SequencedNote>(n => n.Key == "k" + i), template));
            }
            Assert.Equal(0, template.Seq);
            Assert.Equal(new[] { 1, 2, 3 }, db.Select<SequencedNote>().OrderBy(n => n.Key).Select(n => n.Seq));
        }

        [Theory]
        [MemberData(nameof(Encodings))]
        public void One_new_item_used_for_several_updates_keeps_each_row_s_autonumber_key(StorageFormat format, string password, bool indexing)
        {
            string path = NewDatabase("reused-key");
            using var db = Open(path, password, format, indexing);
            for (int i = 0; i < 3; i++) db.Insert(new NumberedItem { Name = "n" + i });
            var template = new NumberedItem { Name = "same" };
            for (int id = 1; id <= 3; id++)
                Assert.True(db.Update(db.Find<NumberedItem>(x => x.Id == id), template));
            Assert.Equal(0, template.Id);
            Assert.Equal(new[] { "1:same", "2:same", "3:same" }, db.Select<NumberedItem>().OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Name));
        }

        [Fact]
        public void An_update_by_example_keeps_every_row_s_autonumber()
        {
            string path = NewDatabase("by-example");
            using var db = new XodContext(path);
            db.Insert(new SequencedTag { Label = "a" });
            db.Insert(new SequencedTag { Label = "a" });
            Assert.True(db.Update(new SequencedTag { Label = "a" }, new SequencedTag { Label = "b" }));
            Assert.Equal(new[] { "1:b", "2:b" }, db.Select<SequencedTag>().OrderBy(t => t.Seq).Select(t => t.Seq + ":" + t.Label));
        }

        [Fact]
        public void A_seed_that_would_hand_out_zero_or_less_is_refused()
        {
            string path = NewDatabase("seed-zero");
            using var db = new XodContext(path);
            Assert.Contains("IdentitySeed", Assert.Throws<AutonumberSeedException>(() => db.Insert(new ZeroSeedTicket { Key = "a" })).Message);
            Assert.Contains("IdentitySeed", Assert.Throws<AutonumberSeedException>(() => db.Insert(new NegativeSeedTicket { Key = "a" })).Message);
        }

        // A float or double holds whole numbers exactly only up to 2^24 or 2^53: a seed beyond is refused, and a
        // number past it isn't handed out (as a number past an int's range isn't), rather than handed out twice.
        [Fact]
        public void A_number_a_float_or_double_cannot_hold_exactly_is_not_handed_out()
        {
            string path = NewDatabase("float");
            using var db = new XodContext(path);
            Assert.Equal(16_777_216f, db.Insert(new FloatTicket { Key = "a" }).Number);
            Assert.Throws<OverflowException>(() => db.Insert(new FloatTicket { Key = "b" }));
            Assert.Single(db.Select<FloatTicket>());
            Assert.Contains("IdentitySeed", Assert.Throws<AutonumberSeedException>(() => db.Insert(new InexactDoubleSeedTicket { Key = "a" })).Message);
        }

        [Fact]
        public void A_seed_on_a_guid_autonumber_is_refused()
        {
            string path = NewDatabase("guid-seed");
            using var db = new XodContext(path);
            Assert.Contains("Guid", Assert.Throws<AutonumberSeedException>(() => db.Insert(new GuidSeedTicket { Key = "a" })).Message);
        }

        // A type refused when it loads, first used by a read, leaves the context usable: the read lock was taken
        // before the type loaded and never released, so the next transaction and the close failed.
        [Fact]
        public void A_type_refused_on_its_first_read_leaves_the_context_usable()
        {
            string path = NewDatabase("refused-read");
            var db = new XodContext(path);
            Assert.Throws<AutonumberSeedException>(() => db.Select<ZeroIncrementTicket>());
            using (var tx = db.BeginTransaction())
            {
                db.Insert(new NumberedItem { Name = "after" });
                tx.Commit();
            }
            var other = System.Threading.Tasks.Task.Run(() => db.Insert(new SequencedNote { Key = "other" }));
            Assert.True(other.Wait(TimeSpan.FromSeconds(20)), "a write on another thread waited on the leaked lock");
            db.Dispose();
            using var reopened = new XodContext(path);
            Assert.Equal(1, reopened.Count<NumberedItem>());
        }

        // An update that leaves an autonumber out (its filter) doesn't move the counter by the new item's value.
        [Fact]
        public void An_autonumber_an_update_filter_leaves_out_does_not_move_the_counter()
        {
            string path = NewDatabase("filter-counter");
            using var db = new XodContext(path);
            for (int i = 0; i < 3; i++) db.Insert(new SequencedNote { Key = "k" + i });
            var skip = new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = new[] { "Seq" } };
            Assert.True(db.Update(db.Find<SequencedNote>(n => n.Key == "k0"), new SequencedNote { Key = "k0", Seq = 500 }, skip));
            Assert.Equal(1, db.Find<SequencedNote>(n => n.Key == "k0").Seq);
            Assert.Equal(4, db.Insert(new SequencedNote { Key = "k9" }).Seq);
        }

        // Values stored as attributes that an update filter leaves out stay as they were (they were dropped:
        // an autonumber key became 0).
        [Theory]
        [InlineData(UpdateFilterBehavior.Target)]
        [InlineData(UpdateFilterBehavior.Skip)]
        public void An_update_filter_keeps_values_stored_as_attributes(UpdateFilterBehavior behavior)
        {
            string path = NewDatabase("filter-attr-" + behavior);
            using var db = new XodContext(path);
            db.Insert(new AttributeKeyed { Tag = "t1", Name = "one" });
            db.Insert(new AttributeKeyed { Tag = "t2", Name = "two" });
            var filter = behavior == UpdateFilterBehavior.Target
                ? new UpdateFilter { Behavior = behavior, Properties = new[] { "Name" } }
                : new UpdateFilter { Behavior = behavior, Properties = new[] { "Id", "Tag" } };
            Assert.True(db.Update(db.Find<AttributeKeyed>(x => x.Id == 2), new AttributeKeyed { Id = 2, Name = "renamed" }, filter));
            Assert.Equal(new[] { "1:t1:one", "2:t2:renamed" }, db.Select<AttributeKeyed>().OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Tag + ":" + x.Name));
            Assert.Equal("renamed", db.Find<AttributeKeyed>(x => x.Id == 2)?.Name);
        }

        // The index follows the row as written: a unique value an update filter leaves out stays indexed and
        // enforced (the index was rebuilt from the new item, whose value the row didn't take).
        [Fact]
        public void A_unique_value_an_update_filter_leaves_out_stays_indexed()
        {
            string path = NewDatabase("filter-index");
            using var db = new XodContext(path);
            db.Insert(new FilteredContact { Email = "a@x", Name = "a" });
            db.Insert(new FilteredContact { Email = "z@x", Name = "z" });
            // The new item's Email is another row's, but the filter leaves it out: the update is checked against
            // the row as it will be stored, not refused as a duplicate.
            var skip = new UpdateFilter { Behavior = UpdateFilterBehavior.Skip, Properties = new[] { "Email" } };
            Assert.True(db.Update(db.Find<FilteredContact>(c => c.Id == 1), new FilteredContact { Id = 1, Email = "z@x", Name = "renamed" }, skip));
            Assert.Equal("renamed", db.Find<FilteredContact>(c => c.Email == "a@x")?.Name);
            Assert.Equal("z", db.Find<FilteredContact>(c => c.Email == "z@x")?.Name);
            Assert.ThrowsAny<XodException>(() => db.Insert(new FilteredContact { Email = "a@x", Name = "dup" }));
        }
    }
}
